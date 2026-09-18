namespace Scry;

/// <summary>
/// The server-sent-events format a live query travels in. The request is an ordinary
/// <see cref="QueryRequest"/>; the response is a run of events, each a complete
/// <see cref="QueryResponse"/> the client reads exactly as it reads the answer to a single query.
/// </summary>
/// <remarks>
/// <para>
/// Every event carries a <c>data:</c> line, including the two that have nothing to say. A reader built
/// on the platform's own parser never sees an event without one, so a heartbeat written as a bare
/// <c>event:</c> line would keep no connection alive.
/// </para>
/// <para>
/// Exactly one of <see cref="Error"/> and <see cref="End"/> closes a stream the server chose to close.
/// A stream that stops without either was cut, which is the transport's failure rather than the
/// server's decision, and is what a client reconnects after.
/// </para>
/// </remarks>
public static class ScryLive
{
    /// <summary>The media type a live query is served as.</summary>
    public const string ContentType = "text/event-stream";

    /// <summary>The route a live query is asked at, under the pattern <c>MapScry</c> was given.</summary>
    public const string Route = "subscribe";

    /// <summary>
    /// The request header naming the last <see cref="Result"/> this client received, by the identifier
    /// that event carried. A server whose first answer matches it says <see cref="Unchanged"/> instead
    /// of sending the same rows again.
    /// </summary>
    public const string LastEventIdHeader = "Last-Event-ID";

    /// <summary>
    /// An answer: the first one, and then each one that differs from the one before it. Its data is a
    /// <see cref="QueryResponse"/>, and its identifier is what <see cref="LastEventIdHeader"/> sends back.
    /// </summary>
    public const string Result = "result";

    /// <summary>The first answer is the one the client already holds. Carries no data.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>Keeps an idle connection open. Carries no data.</summary>
    public const string Ping = "ping";

    /// <summary>Closes the stream after a failure. Its data is a <see cref="ScryError"/>.</summary>
    public const string Error = "error";

    /// <summary>Closes the stream for a reason of the server's own. Its data is a <see cref="ScryLiveEnd"/>.</summary>
    public const string End = "end";
}
