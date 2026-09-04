namespace Scry;

/// <summary>
/// The <c>#q=</c> share link: a query carried in the URL fragment, which browsers never send to a
/// server — so a shared query cannot land in an access log, a proxy trace, or a referrer header on
/// the way.
/// </summary>
/// <remarks>
/// The encoding is a compatibility contract: links already in circulation have to keep opening, so
/// neither the prefix nor the base64url spelling may change.
/// </remarks>
public static class ShareLinkCodec
{
    /// <summary>The fragment prefix a shared query is carried under.</summary>
    public const string Prefix = "#q=";

    /// <summary>
    /// The query carried by a <c>#q=</c> fragment, or null. A shared link is untrusted input like any
    /// other URL, so anything that does not decode is ignored rather than surfaced — the explorer opens
    /// on its sample query instead of on an error.
    /// </summary>
    public static string? Decode(string? hash)
    {
        if (hash is null ||
            !hash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var encoded = Uri.UnescapeDataString(hash[Prefix.Length..]);
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            // base64url drops the padding; Convert requires it.
            padded = padded.PadRight(padded.Length + (3 - (padded.Length + 3) % 4), '=');
            var code = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (code.Length == 0)
            {
                return null;
            }

            return code;
        }
        catch
        {
            return null;
        }
    }

    // base64url of the UTF-8 text: URL-safe, unpadded, and stable across the round trip above.
    /// <summary>The <c>#q=</c> fragment carrying <paramref name="code"/>.</summary>
    public static string Encode(string code) =>
        Prefix +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(code))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
