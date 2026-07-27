using System.Security.Cryptography;

/// <summary>
/// Encodes and decodes opaque, HMAC-signed keyset paging cursors. A cursor carries the ordering-key
/// values of a page's last row as tagged constants; the server turns them back into a seek predicate.
/// The signature enforces the opaque-cursor contract and rejects tampered or garbage tokens early — it
/// is not an authorization control (a decoded cursor is re-validated and policy-filtered like any
/// other predicate). Shape: <c>base64url(json) "." base64url(hmac)</c>.
/// </summary>
static class CursorCodec
{
    // The signed payload: the tagged ordering-key values of a page's last row.
    sealed record Payload(IReadOnlyList<CursorValue> Keys);

    sealed record CursorValue(string? Value, ClrTypeTag Tag);

    public static string Encode(IReadOnlyList<(string? Value, ClrTypeTag Tag)> keys, byte[] signingKey)
    {
        var payload = new Payload([.. keys.Select(_ => new CursorValue(_.Value, _.Tag))]);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, ScryJson.Options);
        var mac = HMACSHA256.HashData(signingKey, json);
        return $"{Base64Url(json)}.{Base64Url(mac)}";
    }

    public static IReadOnlyList<ConstNode> Decode(string cursor, byte[] signingKey)
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
            json = FromBase64Url(cursor[..dot]);
            mac = FromBase64Url(cursor[(dot + 1)..]);
        }
        catch (FormatException)
        {
            throw Reject();
        }

        var expected = HMACSHA256.HashData(signingKey, json);
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

        if (payload is not { Keys.Count: > 0 })
        {
            throw Reject();
        }

        return [.. payload.Keys.Select(_ => new ConstNode(_.Value, _.Tag))];
    }

    /// <summary>
    /// Converts a runtime ordering-key value into the invariant-string + tag form the wire uses for
    /// constants. Unmapped types ride the <see cref="ClrTypeTag.String"/> tag — the seek side rebinds
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
            DateOnly date => (date.ToString("O", culture), ClrTypeTag.DateOnly),
            Guid guid => (guid.ToString(), ClrTypeTag.Guid),
            byte[] bytes => (Convert.ToBase64String(bytes), ClrTypeTag.Bytes),
            Enum enumeration => (enumeration.ToString(), ClrTypeTag.Enum),
            _ => (Convert.ToString(value, culture), ClrTypeTag.String)
        };
    }

    static ScryValidationException Reject() =>
        new("Invalid paging cursor.");

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid base64url length.")
        };

        return Convert.FromBase64String(padded);
    }
}
