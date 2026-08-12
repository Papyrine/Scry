/// <summary>
/// Encodes and decodes opaque, HMAC-signed keyset paging cursors. A cursor carries the ordering-key
/// values of a page's last row as tagged constants; the server turns them back into a seek predicate.
/// The signature enforces the opaque-cursor contract and rejects tampered or garbage tokens early — it
/// is not an authorization control (a decoded cursor is re-validated and policy-filtered like any
/// other predicate). Shape: <c>base64url(json) "." base64url(hmac)</c>.
/// </summary>
static class CursorCodec
{
    // The signed payload: the tagged ordering-key values of a page's last row, plus a stamp of the
    // ordering they were read in — see OrderStamp.
    sealed record Payload(IReadOnlyList<CursorValue> Keys, string Order);

    sealed record CursorValue(string? Value, ClrTypeTag Tag);

    public static string Encode(IReadOnlyList<(string? Value, ClrTypeTag Tag)> keys, string order, byte[] signingKey)
    {
        var payload = new Payload([.. keys.Select(_ => new CursorValue(_.Value, _.Tag))], order);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, ScryJson.Options);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(signingKey, json, mac);
        return $"{Base64Url.EncodeToString(json)}.{Base64Url.EncodeToString(mac)}";
    }

    /// <summary>
    /// Identifies the ordering a cursor belongs to: the source it read, and every ordering key's path
    /// and direction — including the primary key appended as the tiebreaker, since that is part of the
    /// order actually seeked. Compared on resume so a cursor cannot be applied to an ordering it was
    /// not issued for.
    /// </summary>
    /// <remarks>
    /// The <b>ordering</b> rather than the whole pipeline, deliberately. Every way a cursor can produce
    /// a wrong page is a change to the order it resumes: a different key, a different direction, a
    /// different source. A changed filter is not one of those — seeking to "the rows of this set
    /// ordered after this key" stays well defined when the set narrows, so a client may filter further
    /// between pages, which forcing an identical pipeline would have refused.
    /// <para>
    /// Only ever compared against another stamp from this same server, and inside an HMAC-signed
    /// payload, so it is a fingerprint rather than a security boundary: it exists to catch a client
    /// changing its ordering, not to withstand one forging a cursor.
    /// </para>
    /// </remarks>
    public static string OrderStamp(string source, IReadOnlyList<(Node Key, bool Descending)> keys)
    {
        var builder = new StringBuilder();
        // Versions the canonical form, so a future change to what is stamped cannot silently match a
        // cursor minted under the old form.
        builder.Append("scry-order-v1\n");
        builder.Append(source).Append('\n');
        foreach (var (key, descending) in keys)
        {
            // Every seek key is a single-segment member (PlanSeek admits nothing else); anything that
            // is not stamps as its node kind, which differs from any member path and so still parts
            // two orderings that are not the same.
            builder.Append(key is MemberNode member ? string.Join(".", member.Path) : key.GetType().Name);
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
            : (rented = ArrayPool<byte>.Shared.Rent(maximum));
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
        var dot = cursor.IndexOf('.');
        if (dot <= 0 ||
            dot == cursor.Length - 1)
        {
            throw Reject();
        }

        byte[] json;
        byte[] mac;
        try
        {
            json = Base64Url.DecodeFromChars(cursor.AsSpan(0, dot));
            mac = Base64Url.DecodeFromChars(cursor.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            throw Reject();
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(signingKey, json, expected);
        if (!CryptographicOperations.FixedTimeEquals(mac, expected))
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
            DateTime date => (date.ToString("O", culture), ClrTypeTag.DateTime),
            Date date => (date.ToString("O", culture), ClrTypeTag.DateOnly),
            Guid guid => (guid.ToString(), ClrTypeTag.Guid),
            byte[] bytes => (Convert.ToBase64String(bytes), ClrTypeTag.Bytes),
            Enum enumeration => (enumeration.ToString(), ClrTypeTag.Enum),
            _ => (Convert.ToString(value, culture), ClrTypeTag.String)
        };
    }

    static ScryValidationException Reject() =>
        new("Invalid paging cursor.");
}
