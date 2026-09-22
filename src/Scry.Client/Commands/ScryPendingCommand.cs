namespace Scry;

/// <summary>
/// A command the client stopped waiting for while the server was still handling it, as listed in
/// <see cref="ScryClient.PendingWork"/> — from when it went pending until it is cleared, or has lingered
/// there <see cref="ScryPendingWorkStore.CompletedLinger"/> after completing.
/// </summary>
/// <remarks>
/// Changes as the command does. Where <see cref="ScryClient.SendCommandAsync{TCommand}"/> was called on
/// a <see cref="SynchronizationContext"/> — a UI thread, a Blazor renderer — it changes there, and
/// <see cref="ScryPendingWorkStore.Changed"/> is raised there straight after.
/// </remarks>
public sealed class ScryPendingCommand
{
    internal ScryPendingCommand(
        string command,
        Guid id,
        string? target,
        IReadOnlyList<string?> keys,
        DateTimeOffset sent,
        Task<ScryCommandOutcome> completion)
    {
        Command = command;
        Id = id;
        Target = target;
        Keys = keys;
        Sent = sent;
        Completion = completion;
    }

    /// <summary>The command's name on the wire.</summary>
    public string Command { get; }

    /// <summary>The id this client gave the command.</summary>
    public Guid Id { get; }

    /// <summary>The source a targeted command acts on, or null for an untargeted one.</summary>
    public string? Target { get; }

    /// <summary>
    /// The target's key, as the command carries it, in the target's key order and in its wire spelling —
    /// for naming the row the command acts on. Empty for an untargeted command.
    /// </summary>
    public IReadOnlyList<string?> Keys { get; }

    /// <summary>When the command was sent.</summary>
    public DateTimeOffset Sent { get; }

    /// <summary>When its outcome arrived, or null while it is pending.</summary>
    public DateTimeOffset? Finished { get; internal set; }

    /// <summary>Where the command is: <see cref="ScryCommandStatus.Pending"/> until its outcome arrives.</summary>
    public ScryCommandStatus Status { get; internal set; } = ScryCommandStatus.Pending;

    /// <summary>Why it failed, or why its outcome is unknown.</summary>
    public string? Error { get; internal set; }

    /// <summary>How long it has taken, to now while it is pending.</summary>
    public TimeSpan Elapsed => (Finished ?? DateTimeOffset.Now) - Sent;

    /// <summary>The final outcome. Never faults.</summary>
    public Task<ScryCommandOutcome> Completion { get; }
}
