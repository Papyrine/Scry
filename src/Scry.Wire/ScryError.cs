namespace Scry.Wire;

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
}
