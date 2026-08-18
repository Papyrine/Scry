namespace Scry;

/// <summary>
/// How a request is written into the <see cref="QueryUrl.Parameter"/> value of a URL. Part of the wire
/// contract, but not a property of the request: a server accepts either form on any query, so this is
/// a choice each sender makes for itself.
/// </summary>
/// <remarks>
/// The two are told apart by their first character and never collide. A query string arrives
/// percent-decoded, so JSON is read back as itself and always opens with <c>{</c>; base64url's alphabet
/// is letters, digits, <c>-</c> and <c>_</c>, which cannot produce one.
/// </remarks>
public enum QueryUrlEncoding
{
    /// <summary>
    /// The request's JSON, percent-encoded. The default: a URL that carries it says what it asked for to
    /// anything that reads URLs — a proxy trace, an access log, the network pane of a browser — without
    /// a decoding step in between, which is worth more on most deployments than the length it costs.
    /// </summary>
    Json,

    /// <summary>
    /// base64url of the request's UTF-8 JSON. Costs 1.33× where percent-encoded JSON costs about 1.84×,
    /// so it is what a sender picks when <see cref="QueryUrl.MaxLength"/> is the binding constraint and
    /// queries would otherwise fall back to bodies — at the price of a URL nothing reads without
    /// decoding it first.
    /// </summary>
    Base64Url
}

/// <summary>
/// The URL form of a request: the serialized <see cref="QueryRequest"/> written into one query-string
/// parameter, so a query can be asked with <c>GET</c> and answered by every cache between the client and
/// the server. Part of the wire contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the URL rather than content on the GET.</b> HTTP itself permits content on a <c>GET</c> —
/// RFC 9110 only says it has no defined semantics and may be rejected — so this is not a protocol
/// restriction. Two other things rule it out. A browser refuses to send one: the Fetch standard throws a
/// <c>TypeError</c> when a body is set with method <c>GET</c>, which rules it out for a WASM client and
/// for the explorer. And an intermediary is permitted to drop the content of a <c>GET</c>: what then
/// reaches the server is still a well-formed request — same method, same URL — carrying nothing to
/// execute, so the server answers 400 and the client that sent a complete request has no way to tell
/// that from a rejection it caused itself. The failure is silent, depends on infrastructure the client
/// cannot see, and does not reproduce locally. A URL survives every hop by construction, which is why
/// the URL is what a server reads and a body arriving on a <c>GET</c> is ignored.
/// </para>
/// <para>
/// <b>Length is bounded, so this form is not always available.</b> 8 KB is the usual server and proxy
/// limit on a whole request line, and what exceeds it is rejected by whichever hop is strictest — as a
/// 414 or a 400, varying by deployment. The budget a client stays inside of is the server's, carried on
/// every response in <see cref="WireFormat.UrlLimitHeader"/>; <see cref="MaxLength"/> is what a sender
/// uses until it has heard one. A query that does not fit is asked with <c>POST</c>, whose body has no
/// such limit — so the encoding a sender picks decides how often that happens, percent-encoded JSON
/// reaching the ceiling on a query about two thirds the size base64url would still fit.
/// </para>
/// <para>
/// <b>What a URL exposes.</b> Everything in the request — including the constants a filter compares
/// against — lands in the access log of every hop, and in the <c>Referer</c> of whatever the page does
/// next. That holds for both encodings: base64url is a shorter spelling, never concealment. A query
/// whose constants are sensitive on their own (an account number, a person's id) is one to ask with
/// <c>POST</c> regardless of length.
/// </para>
/// </remarks>
public static class QueryUrl
{
    /// <summary>The query-string parameter carrying the encoded request. Part of the wire contract.</summary>
    public const string Parameter = "q";

    /// <summary>
    /// The budget a sender uses before a server has told it one, and the default a server advertises.
    /// Deliberately well under the 8 KB request line that servers and proxies commonly cap: a URL
    /// carries more than the parameter, the limit is on the whole line, and the hop that enforces it is
    /// not the one being written against here.
    /// </summary>
    public const int MaxLength = 4096;

    /// <summary>
    /// Whether <paramref name="encoded"/> is short enough to be asked as a URL under
    /// <paramref name="limit"/>. A limit of zero admits nothing, so a deployment that wants no URL form
    /// needs no special case here.
    /// </summary>
    /// <remarks>
    /// Measured on what <see cref="Encode(QueryRequest, QueryUrlEncoding)"/> returned, which is already
    /// escaped — the length that lands in the request line, rather than the length before escaping,
    /// which is not what any hop enforces a limit against.
    /// </remarks>
    public static bool WithinLimit(string encoded, int limit) =>
        encoded.Length <= limit;

    /// <summary>Encodes a request into its <see cref="Parameter"/> value, ready to append to a URL.</summary>
    public static string Encode(QueryRequest request, QueryUrlEncoding encoding = QueryUrlEncoding.Json) =>
        Encode(ScryJson.SerializeToUtf8(request), encoding);

    /// <summary>
    /// Encodes the serialized bytes of a request. Taken as bytes rather than as a request so a sender
    /// that already has them encodes exactly those, with no second serialization in between that could
    /// differ from what it meant to send.
    /// </summary>
    /// <remarks>
    /// The result is escaped, so a caller appends it to a URL as it stands. That matters for
    /// <see cref="QueryUrlEncoding.Json"/>, whose <c>&amp;</c>, <c>=</c>, <c>+</c> and <c>#</c> would
    /// otherwise be read as query-string syntax and silently truncate or corrupt the request;
    /// <see cref="QueryUrlEncoding.Base64Url"/> has nothing to escape and passes through untouched.
    /// </remarks>
    public static string Encode(ReadOnlySpan<byte> utf8, QueryUrlEncoding encoding = QueryUrlEncoding.Json)
    {
        if (encoding == QueryUrlEncoding.Base64Url)
        {
            return Base64Url.EncodeToString(utf8);
        }

        return Uri.EscapeDataString(Encoding.UTF8.GetString(utf8));
    }

    /// <summary>
    /// Decodes a <see cref="Parameter"/> value a client sent, in whichever encoding it used. Fails
    /// closed, as the rest of the wire does: anything that is not a request this server can parse throws
    /// <see cref="ScryWireException"/> rather than producing a partial query.
    /// </summary>
    /// <remarks>
    /// Takes the value already percent-decoded, which is what a query-string parser hands over.
    /// </remarks>
    public static QueryRequest Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            throw new ScryWireException($"The '{Parameter}' query parameter is required.");
        }

        // The one character that separates the two encodings, and the reason neither has to announce
        // itself: base64url cannot produce a '{', and a serialized request cannot begin with anything
        // else.
        if (encoded[0] == '{')
        {
            return ScryJson.DeserializeRequest(Encoding.UTF8.GetBytes(encoded));
        }

        byte[] utf8;
        try
        {
            utf8 = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException exception)
        {
            throw new ScryWireException($"The '{Parameter}' query parameter is neither a JSON request nor valid base64url.", exception);
        }

        return ScryJson.DeserializeRequest(utf8);
    }
}
