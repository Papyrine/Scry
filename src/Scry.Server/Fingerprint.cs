/// <summary>
/// A short, stable name for some bytes: twelve bytes of SHA-256 as sixteen base64url characters. Used
/// where two things only have to be told apart, by a reader who is never meant to learn what they
/// were — a freshness token inside an ETag, one answer of a live query against the next.
/// </summary>
static class Fingerprint
{
    public static string Of(ReadOnlySpan<byte> value)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(value, hash);
        return Base64Url.EncodeToString(hash[..12]);
    }

    public static string Of(string value) =>
        Of(Encoding.UTF8.GetBytes(value));
}
