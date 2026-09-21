namespace Scry;

/// <summary>
/// What a hub transport calls: the method names the two ends of it agree on. Here, where both ends
/// can see them, because neither references the other.
/// </summary>
/// <remarks>
/// Every method takes the request as the JSON the HTTP endpoints take, and answers with the JSON they
/// answer with — as a string, so that it is this library's reader and writer that touch it and never
/// the hub's own. A failure is the one thing a hub has no status line to say, so it is answered as a
/// <see cref="ScryStream.Error"/> marker carrying its <see cref="ScryStreamMarker.Code"/>: a single
/// answer that is one, or the last item of a stream.
/// </remarks>
public static class ScryHubProtocol
{
    /// <summary>One query, one response.</summary>
    public const string Query = "Query";

    /// <summary>Several queries, one response.</summary>
    public const string Batch = "Batch";

    /// <summary>One query, streamed as the lines the stream endpoint writes — markers included.</summary>
    public const string Stream = "Stream";

    /// <summary>One query, answered again whenever its answer changes: a stream of responses.</summary>
    public const string Subscribe = "Subscribe";
}
