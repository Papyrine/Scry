namespace Scry;

/// <summary>
/// Thrown when an incoming query violates the allow-list or a resource limit. The executor fails
/// closed: the query is rejected before any expression is rebound or executed.
/// </summary>
public sealed class ScryValidationException(string message) :
    Exception(message)
{
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
