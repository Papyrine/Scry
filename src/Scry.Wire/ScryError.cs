namespace Scry;

/// <summary>
/// The body of a non-success response from the query endpoint.
/// </summary>
/// <param name="Error">What was rejected, or the fixed execution-failure message for a 500.</param>
public sealed record ScryError(string Error)
{
    /// <summary>
    /// True when the failure is attributed to the request's schema stamp differing from the server's —
    /// a client generated against an older model surface. The typed client surfaces such failures as
    /// <see cref="ScryStaleClientException"/> so one catch can prompt a reload. Omitted from the JSON
    /// when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StaleClient { get; init; }

    /// <summary>
    /// True when the request was refused for the way it travelled rather than for what it asked: it
    /// named a <c>[Sensitive]</c> member alongside a constant while being asked as a URL. The same
    /// request in a body is accepted, so a client that sees this re-sends it that way rather than
    /// failing. Omitted from the JSON when false.
    /// </summary>
    /// <remarks>
    /// A flag rather than a message to match on, because a message is for a person and this is for a
    /// client. It is also why the message says only what to do: naming the member would answer "which
    /// of these columns is the sensitive one?" for anyone who asked.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresBody { get; init; }
}
