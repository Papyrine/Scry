/// <summary>
/// Encodes and decodes opaque, sealed keyset paging cursors. A cursor carries the ordering-key values
/// of a page's last row as tagged constants; the server turns them back into a seek predicate. It is
/// sealed with authenticated encryption (AES-GCM under a key derived from <c>CursorKey</c>),
/// which enforces the opaque-cursor contract two ways: a tampered or garbage token is refused early,
/// and the key values — which can be a <c>[Sensitive]</c> member's — never travel in the clear through
/// the URL of the next page. It is not an authorization control: a decoded cursor is re-validated and
/// policy-filtered like any other predicate. Shape: <c>base64url(nonce || ciphertext || tag)</c>.
/// </summary>
static class CursorCodec
{
    // The signed payload: the tagged ordering-key values of a page's last row, plus a stamp of the
    // ordering they were read in — see OrderStamp.
    sealed record Payload(IReadOnlyList<CursorValue> Keys, string Order);

    sealed record CursorValue(string? Value, ClrTypeTag Tag);

    const int nonceSize = 12;
    const int tagSize = 16;

    public static string Encode(IReadOnlyList<(string? Value, ClrTypeTag Tag)> keys, string order, byte[] signingKey)
    {
        var payload = new Payload([.. keys.Select(_ => new CursorValue(_.Value, _.Tag))], order);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, ScryJson.Options);

        var token = new byte[nonceSize + json.Length + tagSize];
        var nonce = token.AsSpan(0, nonceSize);
        var ciphertext = token.AsSpan(nonceSize, json.Length);
        var tag = token.AsSpan(nonceSize + json.Length, tagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key(signingKey), tagSize);
        aes.Encrypt(nonce, json, ciphertext, tag);
        return Base64Url.EncodeToString(token);
    }

    // The configured key is any length a host chose; AES wants exactly 32 bytes, and hashing it gets
    // there from anything without weakening a key that already was.
    static byte[] Key(byte[] signingKey) =>
        SHA256.HashData(signingKey);

    /// <summary>
    /// Identifies the ordering a cursor belongs to: the source it read, the steps that changed what
    /// its rows are (a flatten, a narrowing), and every ordering key's path and direction — including
    /// the primary key appended as the tiebreaker, since that is part of the order actually seeked.
    /// Compared on resume so a cursor cannot be applied to an ordering it was not issued for.
    /// </summary>
    /// <remarks>
    /// The <b>ordering</b> rather than the whole pipeline, deliberately. Every way a cursor can produce
    /// a wrong page is a change to the order it resumes: a different key, a different direction, a
    /// different source. A changed filter is not one of those — seeking to "the rows of this set
    /// ordered after this key" stays well defined when the set narrows, so a client may filter further
    /// between pages, which forcing an identical pipeline would have refused. A flatten or a
    /// narrowing is not a filter: it changes which rows the keys are read off, so
    /// <c>Fleet.SelectMany(Machines).OrderBy(Name)</c> and <c>Fleet.OrderBy(Name)</c> stamp apart
    /// even where both types spell their keys the same.
    /// <para>
    /// Only ever compared against another stamp from this same server, and inside a sealed payload,
    /// so it is a fingerprint rather than a security boundary: it exists to catch a client changing
    /// its ordering, not to withstand one forging a cursor.
    /// </para>
    /// </remarks>
    public static string OrderStamp(string source, IReadOnlyList<string> shape, IReadOnlyList<(Node Key, bool Descending)> keys)
    {
        var builder = new StringBuilder();
        // Versions the canonical form, so a future change to what is stamped cannot silently match a
        // cursor minted under the old form.
        builder.Append("scry-order-v2\n");
        builder.Append(source).Append('\n');
        foreach (var step in shape)
        {
            builder.Append(step).Append('\n');
        }

        foreach (var (key, descending) in keys)
        {
            // Every seek key is a single-segment member (PlanSeek admits nothing else); anything that
            // is not stamps as its node kind, which differs from any member path and so still parts
            // two orderings that are not the same.
            builder.Append(key is MemberNode member ? string.Join('.', member.Path) : key.GetType().Name);
            builder.Append(descending ? " desc\n" : " asc\n");
        }

        // Encoded into a stack buffer rather than a byte[] of its own: the canonical form is a source
        // name and a handful of member paths, so it comfortably fits one, and a rented array covers
        // the pathological case rather than the common one paying for it.
        var canonical = builder.ToString();
        var maximum = Encoding.UTF8.GetMaxByteCount(canonical.Length);
        byte[]? rented = null;
        var utf8 = maximum <= 512
            ? stackalloc byte[512]
            : rented = ArrayPool<byte>.Shared.Rent(maximum);
        try
        {
            var written = Encoding.UTF8.GetBytes(canonical, utf8);
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(utf8[..written], hash);
            // 96 bits, matching the schema stamp's reasoning: compared pairwise rather than searched,
            // so the birthday bound does not apply, and 12 divides by 3 so the base64 needs no padding.
            return Base64Url.EncodeToString(hash[..12]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public static (IReadOnlyList<ConstNode> Values, string Order) Decode(string cursor, byte[] signingKey)
    {
        byte[] token;
        try
        {
            token = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            throw Reject();
        }

        // Nothing sealed is shorter than its nonce and tag around an empty document, and a document
        // is never empty.
        if (token.Length <= nonceSize + tagSize)
        {
            throw Reject();
        }

        var json = new byte[token.Length - nonceSize - tagSize];
        try
        {
            using var aes = new AesGcm(Key(signingKey), tagSize);
            aes.Decrypt(
                token.AsSpan(0, nonceSize),
                token.AsSpan(nonceSize, json.Length),
                token.AsSpan(nonceSize + json.Length, tagSize),
                json);
        }
        catch (CryptographicException)
        {
            throw Reject();
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, ScryJson.Options);
        }
        catch (JsonException)
        {
            throw Reject();
        }

        // A cursor minted before the ordering stamp existed carries no Order and cannot be shown to
        // belong to this query, so it is refused rather than trusted — the same "resume point lost"
        // a restart under the ephemeral signing key already produces, and the safe direction.
        if (payload is not { Keys.Count: > 0, Order.Length: > 0 })
        {
            throw Reject();
        }

        return ([.. payload.Keys.Select(_ => new ConstNode(_.Value, _.Tag))], payload.Order);
    }

    /// <summary>
    /// Converts a runtime ordering-key value into the invariant-string + tag form the wire uses for
    /// constants. Unmapped types use the <see cref="ClrTypeTag.String"/> tag — the seek side rebinds
    /// each value against the ordering member's real CLR type, so the tag is only a hint (matching the
    /// client's constant encoding).
    /// </summary>
    /// <remarks>
    /// The server's half of the mapping <c>ValueTag</c> makes on the client, which the two packages
    /// cannot share: a key spelled here differently from the constant the same value becomes there is
    /// a key the seek predicate compares against something else. The temporal spellings in particular
    /// are round-trip forms rather than default ones — a key truncated to the second or the minute
    /// seeks from a row boundary that is not the one the page ended on, which repeats or skips rows.
    /// </remarks>
    public static (string? Value, ClrTypeTag Tag) TagValue(object? value)
    {
        var culture = CultureInfo.InvariantCulture;
        return value switch
        {
            null => (null, ClrTypeTag.Null),
            string text => (text, ClrTypeTag.String),
            bool flag => (flag.ToString(), ClrTypeTag.Boolean),
            int number => (number.ToString(culture), ClrTypeTag.Int32),
            long number => (number.ToString(culture), ClrTypeTag.Int64),
            decimal number => (number.ToString(culture), ClrTypeTag.Decimal),
            double number => (number.ToString(culture), ClrTypeTag.Double),
            // A local Kind is flattened to the wall clock the provider binds anyway, so a fleet whose
            // servers sit in different zones decodes a cursor to the row the encoding one ended on.
            DateTime {Kind: DateTimeKind.Local} local => (local.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", culture), ClrTypeTag.DateTime),
            DateTime date => (date.ToString("O", culture), ClrTypeTag.DateTime),
            Date date => (date.ToString("O", culture), ClrTypeTag.DateOnly),
            DateTimeOffset stamped => (stamped.ToString("O", culture), ClrTypeTag.String),
            Time time => (time.ToString("O", culture), ClrTypeTag.String),
            Guid guid => (guid.ToString(), ClrTypeTag.Guid),
            byte[] bytes => (Convert.ToBase64String(bytes), ClrTypeTag.Bytes),
            Enum enumeration => (enumeration.ToString(), ClrTypeTag.Enum),
            _ => (Convert.ToString(value, culture), ClrTypeTag.String)
        };
    }

    static ScryValidationException Reject() =>
        new("Invalid paging cursor.");
}
