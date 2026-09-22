namespace Scry;

/// <summary>
/// What sending a command came to: its outcome where the server decided it within
/// <see cref="ScryClient.CommandWait"/>, and otherwise <see cref="ScryCommandStatus.Pending"/>, with the
/// outcome to follow on <see cref="Completion"/>.
/// </summary>
/// <remarks>
/// A refusal is not an outcome. A command the server would not accept — malformed, denied, one too
/// many — was never run, so sending it throws, as a query the server refuses does. An outcome is what
/// became of a command the server did accept.
/// </remarks>
public class ScryCommandOutcome
{
    private protected ScryCommandOutcome(
        string command,
        Guid id,
        ScryCommandStatus status,
        JsonElement? result,
        string? error,
        ScryPendingCommand? pending)
    {
        Command = command;
        Id = id;
        Status = status;
        Result = result;
        Error = error;
        Pending = pending;
    }

    internal static ScryCommandOutcome Final(string command, Guid id, ScryCommandStatus status, JsonElement? result, string? error)
    {
        var outcome = new ScryCommandOutcome(command, id, status, result, error, pending: null);
        outcome.Completion = Task.FromResult(outcome);
        return outcome;
    }

    internal static ScryCommandOutcome InFlight(string command, Guid id, ScryPendingCommand pending, Task<ScryCommandOutcome> completion) =>
        new(command, id, ScryCommandStatus.Pending, result: null, error: null, pending)
        {
            Completion = completion
        };

    /// <summary>The command's name on the wire.</summary>
    public string Command { get; }

    /// <summary>
    /// The id this client gave the command, which the server knows it by: what its receipts carry, what
    /// a pending one is asked for again by, and what the audit trail records.
    /// </summary>
    public Guid Id { get; }

    /// <summary>Where the command is.</summary>
    public ScryCommandStatus Status { get; }

    /// <summary>What the handler answered with, as JSON, for a completed command that has a result.</summary>
    public JsonElement? Result { get; }

    /// <summary>
    /// Why a failed command failed, or why an unknown one is unknown. A handler's failure reads as the
    /// message it chose to show, or as a fixed one: nothing internal leaves the server this way.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// The command's entry in <see cref="ScryClient.PendingWork"/>, on a <see cref="ScryCommandStatus.Pending"/>
    /// outcome. Null on every final one.
    /// </summary>
    public ScryPendingCommand? Pending { get; }

    /// <summary>
    /// The final outcome. Complete already, as this outcome itself, where the outcome is final. Never
    /// faults: a connection lost for good is an <see cref="ScryCommandStatus.Unknown"/> outcome.
    /// </summary>
    public Task<ScryCommandOutcome> Completion { get; private protected set; } = null!;

    /// <summary>
    /// This outcome, where the command completed. Throws <see cref="ScryCommandFailedException"/> where
    /// it failed or its outcome is unknown, and <see cref="InvalidOperationException"/> where it is
    /// still pending — await <see cref="Completion"/> first to wait for it.
    /// </summary>
    public ScryCommandOutcome EnsureCompleted()
    {
        Check();
        return this;
    }

    private protected void Check()
    {
        if (Status == ScryCommandStatus.Completed)
        {
            return;
        }

        if (Status == ScryCommandStatus.Pending)
        {
            throw new InvalidOperationException($"'{Command}' is still pending. Await its Completion for the outcome.");
        }

        throw new ScryCommandFailedException(this);
    }

    private protected void Complete(Task<ScryCommandOutcome> completion) =>
        Completion = completion;
}

/// <summary>A <see cref="ScryCommandOutcome"/> for a command that answers with a result.</summary>
/// <typeparam name="TResult">The class the command answers with.</typeparam>
public sealed class ScryCommandOutcome<TResult> :
    ScryCommandOutcome
{
    bool read;
    TResult value = default!;

    ScryCommandOutcome(ScryCommandOutcome outcome) :
        base(outcome.Command, outcome.Id, outcome.Status, outcome.Result, outcome.Error, outcome.Pending)
    {
    }

    internal static ScryCommandOutcome<TResult> From(ScryCommandOutcome outcome)
    {
        var typed = new ScryCommandOutcome<TResult>(outcome);
        if (outcome.Status == ScryCommandStatus.Pending)
        {
            typed.Completion = Typed(outcome.Completion);
        }
        else
        {
            typed.Completion = Task.FromResult(typed);
        }

        typed.Complete(Untyped(typed.Completion));
        return typed;
    }

    static async Task<ScryCommandOutcome<TResult>> Typed(Task<ScryCommandOutcome> completion) =>
        From(await completion.ConfigureAwait(false));

    static async Task<ScryCommandOutcome> Untyped(Task<ScryCommandOutcome<TResult>> completion) =>
        await completion.ConfigureAwait(false);

    /// <summary>The final outcome, with its result typed. Complete already where this outcome is final.</summary>
    public new Task<ScryCommandOutcome<TResult>> Completion { get; private set; } = null!;

    /// <summary>
    /// The command's result. Throws unless the command completed — see
    /// <see cref="ScryCommandOutcome.EnsureCompleted"/> for what it throws — so read
    /// <see cref="ScryCommandOutcome.Status"/> first where a failure is expected.
    /// </summary>
    public TResult Value
    {
        get
        {
            Check();
            if (!read)
            {
                value = Result is { } element ? element.Deserialize<TResult>(ScryJson.Options)! : default!;
                read = true;
            }

            return value;
        }
    }

    /// <inheritdoc cref="ScryCommandOutcome.EnsureCompleted"/>
    public new ScryCommandOutcome<TResult> EnsureCompleted()
    {
        Check();
        return this;
    }
}
