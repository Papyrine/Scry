/// <summary>
/// One accepted command: its id, whose it is, and — once it has finished — its outcome. The receipt a
/// client asking for it again is answered with, and the task a pending answer waits on.
/// </summary>
sealed class CommandRecord(CommandRequest request, CommandMeta meta, string? caller, DateTimeOffset accepted, string stamp)
{
    TaskCompletionSource<CommandReceipt> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; } = request.Id;

    /// <summary>The command as it arrived, for the audit trail's second entry.</summary>
    public CommandRequest Request { get; } = request;

    public CommandMeta Meta { get; } = meta;

    public string? Caller { get; } = caller;

    public DateTimeOffset Accepted { get; } = accepted;

    public long AcceptedTimestamp { get; } = Stopwatch.GetTimestamp();

    /// <summary>When it finished, or null while it is pending. Read and written under the tracker's lock.</summary>
    public DateTimeOffset? Finished { get; private set; }

    /// <summary>The receipt as it stands: pending until the command finishes, and its outcome after.</summary>
    public CommandReceipt Current { get; private set; } =
        CommandReceipt.Create(request.Id, CommandStatus.Pending) with
        {
            Stamp = stamp
        };

    /// <summary>Completes with the final receipt. Never faults.</summary>
    public Task<CommandReceipt> Completion => completion.Task;

    /// <summary>
    /// Set where the command was answered as pending, so its finishing is audited as a second entry —
    /// from a scope of its own, since the request that sent it is long gone.
    /// </summary>
    public IServiceScopeFactory? AuditScopes { get; set; }

    /// <summary>The real failure, for the audit trail, where the client was shown a fixed message.</summary>
    public Exception? Failure { get; private set; }

    // Called under the tracker's lock, once: from here the command reads as finished to everything
    // that asks, while whoever awaits its outcome is not told until Release.
    public void Finish(CommandReceipt receipt, DateTimeOffset at, Exception? failure)
    {
        Finished = at;
        Failure = failure;
        Current = receipt;
    }

    // Called once, outside the lock, after what finishing sets off — the change it reports, its audit
    // entry — so a waiter handed the outcome finds those done. The continuations run elsewhere, since
    // the completion was made with RunContinuationsAsynchronously.
    public void Release() =>
        completion.TrySetResult(Current);
}
