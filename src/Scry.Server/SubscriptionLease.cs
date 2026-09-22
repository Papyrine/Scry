/// <summary>
/// One live query's place in the <see cref="SubscriptionHub"/>: what it is listening for, whether it
/// is due, and when it may next run. Disposed when the live query ends, which gives the place back.
/// </summary>
/// <remarks>
/// <para>
/// Due is a flag, not a queue. However many changes arrive while a run is in progress, a slow reader
/// is being written to, or the throttle is being waited out, the query runs once afterwards and
/// answers for all of them — there is never a backlog of stale answers, because no answer is made
/// until it can be sent.
/// </para>
/// <para>
/// What it listens for is whatever its last run read. Until a run has said, and while one is in
/// progress — it may read something the run before did not — every change counts.
/// </para>
/// </remarks>
sealed class SubscriptionLease(SubscriptionHub hub, string? caller, ScryOptions options) :
    IDisposable
{
    Lock gate = new();
    TaskCompletionSource due = NewSignal();
    IReadOnlySet<string>? dependencies;
    bool running = true;
    long lastRun = Stopwatch.GetTimestamp();
    int disposed;

    public string? Caller => caller;

    public void Offer(ScryChange change)
    {
        lock (gate)
        {
            if (running ||
                change.Everything ||
                dependencies is null ||
                dependencies.Overlaps(change.Entities))
            {
                due.TrySetResult();
            }
        }
    }

    public void MarkDue()
    {
        lock (gate)
        {
            due.TrySetResult();
        }
    }

    /// <summary>Whatever was due before now is what this run answers; whatever arrives from here is not.</summary>
    public void BeginRun()
    {
        lock (gate)
        {
            running = true;
            due = NewSignal();
        }

        lastRun = Stopwatch.GetTimestamp();
    }

    public void EndRun(IReadOnlySet<string>? read)
    {
        lock (gate)
        {
            running = false;
            dependencies = read;
        }
    }

    /// <summary>
    /// Completes when the query should run again: a change it listens for was reported or its poll
    /// came round, and the throttle since its last run has passed.
    /// </summary>
    public async ValueTask WaitUntilDue(Cancel cancel = default)
    {
        Task signal;
        lock (gate)
        {
            signal = due.Task;
        }

        if (options.SubscriptionPollInterval is { } poll)
        {
            await Either(signal, poll - Stopwatch.GetElapsedTime(lastRun), cancel);
        }
        else
        {
            await signal.WaitAsync(cancel);
        }

        var remaining = options.SubscriptionThrottle - Stopwatch.GetElapsedTime(lastRun);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancel);
        }
    }

    static async Task Either(Task signal, TimeSpan timeout, Cancel cancel)
    {
        if (timeout <= TimeSpan.Zero)
        {
            cancel.ThrowIfCancellationRequested();
            return;
        }

        // Linked so that a signal arriving first releases the timer rather than leaving it to run out.
        using var timer = CancelSource.CreateLinkedTokenSource(cancel);
        var first = await Task.WhenAny(signal, Task.Delay(timeout, timer.Token));
        await timer.CancelAsync();
        if (first != signal)
        {
            cancel.ThrowIfCancellationRequested();
        }
    }

    // Completed from inside the host's SaveChanges. Without this its continuation — a query against
    // the database — would run there too, on the writer's thread and inside the writer's call.
    static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            hub.Leave(this);
        }
    }
}
