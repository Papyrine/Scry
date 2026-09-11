namespace Scry;

/// <summary>
/// The body of a non-success response from the query endpoint.
/// </summary>
/// <param name="Error">What was rejected, or the fixed execution-failure message for a 500.</param>
public sealed record ScryError(string Error)
{
    /// <summary>
    /// Which of the endpoint's answers this is. The message is for a person; this is what a client
    /// branches on. Omitted from the JSON when <see cref="ScryErrorCode.Unknown"/>, which the endpoint
    /// never writes — a body carrying no code is one this client did not get from a Scry endpoint.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ScryErrorCode Code { get; init; }

    /// <summary>
    /// True when the request was refused for the way it travelled rather than for what it asked: it
    /// named a <c>[Sensitive]</c> member alongside a constant while being asked as a URL. The same
    /// request in a body is accepted, so a client that sees this re-sends it that way rather than
    /// failing. Omitted from the JSON when false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A flag rather than a message to match on, because a message is for a person and this is for a
    /// client. It is also why the message says only what to do: naming the member would answer "which
    /// of these columns is the sensitive one?" for anyone who asked.
    /// </para>
    /// <para>
    /// A flag rather than a <see cref="Code"/> because it is a different axis: it says what to do
    /// next, not what went wrong, and it rides on a <see cref="ScryErrorCode.StaleClient"/> rejection
    /// as readily as on a plain <see cref="ScryErrorCode.Validation"/> one — a client generated before
    /// the member was marked is exactly the client that hits this, and re-sending in a body is still
    /// the answer whatever it does about regenerating.
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresBody { get; init; }
}
