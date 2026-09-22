namespace Scry;

/// <summary>
/// Thrown by <see cref="ScryCommandOutcome.EnsureCompleted"/> for a command that was accepted and then
/// failed, or whose outcome is unknown.
/// </summary>
/// <remarks>
/// Not what a refused command throws: one the server would not accept was never run, and throws the
/// refusal itself — <see cref="ScryRequestException"/>, <see cref="ScryPermissionException"/> or
/// <see cref="ScryStaleClientException"/>, as a query the server refuses does.
/// </remarks>
public sealed class ScryCommandFailedException :
    Exception
{
    public ScryCommandFailedException(ScryCommandOutcome outcome) :
        base(Describe(outcome)) =>
        Outcome = outcome;

    /// <summary>The outcome, with the reason on its <see cref="ScryCommandOutcome.Error"/>.</summary>
    public ScryCommandOutcome Outcome { get; }

    static string Describe(ScryCommandOutcome outcome)
    {
        if (outcome.Status == ScryCommandStatus.Unknown)
        {
            return $"'{outcome.Command}' has no known outcome, and may have run: {outcome.Error}";
        }

        return $"'{outcome.Command}' failed: {outcome.Error ?? "no reason given"}";
    }
}
