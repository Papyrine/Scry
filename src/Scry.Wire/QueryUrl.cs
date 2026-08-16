namespace Scry;

/// <summary>
/// The URL form of a request: the serialized <see cref="QueryRequest"/> base64url-encoded into one
/// query-string parameter, so a query can be asked with <c>GET</c> and answered by every cache between
/// the client and the server. Part of the wire contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the URL rather than content on the GET.</b> A body would carry any query at any size, and it
/// cannot be used. A browser refuses to send one at all — the Fetch standard forbids content on
/// <c>GET</c>, which rules it out for a WASM client and for the explorer. And an intermediary is
/// permitted to drop the content of a <c>GET</c>: what then reaches the server is still a well-formed
/// request — same method, same URL — carrying nothing to execute, so the server answers 400 and the
/// client that sent a complete request has no way to tell that from a rejection it caused itself. The
/// failure is silent, depends on infrastructure the client cannot see, and does not reproduce locally.
/// A URL survives every hop by construction.
/// </para>
/// <para>
/// <b>Why base64url rather than the JSON percent-encoded.</b> Length is the binding constraint here, and
/// percent-encoding JSON inflates it by about 1.84× where base64url costs 1.33×. base64url also has no
/// reserved characters to escape, so what the client writes is what the server reads.
/// </para>
/// <para>
/// <b>Length is bounded, so this form is not always available.</b> 8 KB is the usual server and proxy
/// limit on a whole request line, and what exceeds it is rejected by whichever hop is strictest — as a
/// 414 or a 400, varying by deployment. <see cref="WithinLimit"/> answers whether an encoded query fits
/// under <see cref="MaxLength"/>, which is set well below that ceiling; a query that does not fit is
/// asked with <c>POST</c>, whose body has no such limit.
/// </para>
/// <para>
/// <b>What a URL exposes.</b> Everything in the request — including the constants a filter compares
/// against — lands in the access log of every hop, and in the <c>Referer</c> of whatever the page does
/// next. A query whose constants are sensitive on their own (an account number, a person's id) is one
/// to ask with <c>POST</c> regardless of length.
/// </para>
/// </remarks>
public static class QueryUrl
{
    /// <summary>The query-string parameter carrying the encoded request. Part of the wire contract.</summary>
    public const string Parameter = "q";

    /// <summary>
    /// The longest encoded request this form is used for. Deliberately well under the 8 KB request line
    /// that servers and proxies commonly cap: a URL carries more than the parameter, the limit is on the
    /// whole line, and the hop that enforces it is not the one being written against here.
    /// </summary>
    public const int MaxLength = 4096;

    /// <summary>Whether <paramref name="encoded"/> is short enough to be asked as a URL.</summary>
    public static bool WithinLimit(string encoded) =>
        encoded.Length <= MaxLength;

    /// <summary>Encodes a request into its <see cref="Parameter"/> value.</summary>
    public static string Encode(QueryRequest request) =>
        Encode(ScryJson.SerializeToUtf8(request));

    /// <summary>
    /// Encodes the serialized bytes of a request. Taken as bytes rather than as a request so a sender
    /// that already has them — every sender that also fingerprints what it sends — encodes exactly what
    /// it hashed, with no second serialization in between that could differ.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> utf8) =>
        Base64Url.EncodeToString(utf8);

    /// <summary>
    /// The fingerprint of the request a URL carries, or null when the parameter is absent or not
    /// base64url. The same value the sender puts in <see cref="WireFormat.QueryHashHeader"/>, because it
    /// is computed over the same bytes — so a cache keyed on one is keyed on the other, whichever way
    /// the query arrived. Reads the encoding only; the request itself is never parsed here.
    /// </summary>
    public static string? TryFingerprint(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        try
        {
            return QueryFingerprint.Compute(Base64Url.DecodeFromChars(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a <see cref="Parameter"/> value a client sent. Fails closed, as the rest of the wire
    /// does: anything that is not base64url of a request this server can parse throws
    /// <see cref="ScryWireException"/> rather than producing a partial query.
    /// </summary>
    public static QueryRequest Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            throw new ScryWireException($"The '{Parameter}' query parameter is required.");
        }

        byte[] utf8;
        try
        {
            utf8 = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException exception)
        {
            throw new ScryWireException($"The '{Parameter}' query parameter is not valid base64url.", exception);
        }

        return ScryJson.DeserializeRequest(utf8);
    }
}
