namespace Scry;

/// <summary>
/// Thrown when an incoming query violates the allow-list or a resource limit. The executor fails
/// closed: the query is rejected before any expression is rebound or executed.
/// </summary>
public sealed class ScryValidationException(string message) :
    Exception(Bounded(message))
{
    /// <summary>
    /// The most of a message that is kept. A rejection names what it rejected, and what it rejected
    /// is often the client's own text — a root name, a member path, a constant — so an unbounded
    /// message would hand a client a way to have its own megabytes echoed into the response body,
    /// the audit trail, and the trace. No message of the server's own comes near the bound.
    /// </summary>
    public const int MaxMessageLength = 1024;

    static string Bounded(string message) =>
        message.Length <= MaxMessageLength
            ? message
            : string.Concat(message.AsSpan(0, MaxMessageLength), "…");

    /// <summary>
    /// True when the rejection is attributed to the request's schema stamp differing from the
    /// server's — a stale client rather than an invalid query. Set by <see cref="ScryProcessor"/>;
    /// the HTTP endpoint forwards it as <see cref="ScryError.StaleClient"/>.
    /// </summary>
    public bool StaleClient { get; init; }

    /// <summary>
    /// True when the query was refused for the way it travelled rather than for what it asked: it
    /// compared a <c>[Sensitive]</c> member against a constant while being asked as a URL. The HTTP
    /// endpoint forwards it as <see cref="ScryError.RequiresBody"/>, and a client re-sends the same
    /// request in a body rather than failing.
    /// </summary>
    public bool RequiresBody { get; init; }
}
