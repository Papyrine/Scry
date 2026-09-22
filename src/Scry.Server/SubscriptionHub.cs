/// <summary>
/// Everything the live queries of one processor share: how many there may be, what tells them a change
/// happened, how many of them may be at the database at once, and the change probe that runs while any
/// of them is open.
/// </summary>
/// <remarks>
/// Nothing here runs while there are no live queries. The listener on <see cref="ScryChanges"/> is
/// taken when the first one opens and given back when the last one closes — which is also what starts
/// and stops that node listening on a backplane — and the probe loop has the same life.
/// </remarks>
sealed class SubscriptionHub(ScryOptions options, ScryChanges changes)
{
    Lock gate = new();
    SubscriptionLease[] leases = [];
    Dictionary<string, int> callers = new(StringComparer.Ordinal);
    SemaphoreSlim slots = new(options.MaxConcurrentSubscriptionRuns);
    IDisposable? listening;
    CancelSource? probing;

    /// <summary>
    /// Admits one live query, or refuses it. Admitted, it is already listening for changes when this
    /// returns — before its first run, so nothing written between that run and its registration can
    /// fall in the gap.
    /// </summary>
    public SubscriptionLease Enter(string? caller, IServiceProvider services)
    {
        SubscriptionLease lease;
        lock (gate)
        {
            if (options.MaxSubscriptions <= 0)
            {
                throw new($"Live queries are off: ScryOptions.{nameof(options.MaxSubscriptions)} is zero. Set it to how many this server may hold open at once.");
            }

            if (leases.Length >= options.MaxSubscriptions)
            {
                throw new ScrySubscriptionLimitException(
                    "This server is holding as many live queries as it allows. Ask again shortly.",
                    perCaller: false);
            }

            if (caller is not null)
            {
                var held = callers.GetValueOrDefault(caller);
                if (held >= options.MaxSubscriptionsPerCaller)
                {
                    throw new ScrySubscriptionLimitException(
                        "This caller is holding as many live queries as it is allowed. Close one, or ask again shortly.",
                        perCaller: true);
                }

                callers[caller] = held + 1;
            }

            lease = new(this, caller, options);
            leases = [.. leases, lease];
            if (leases.Length == 1)
            {
                Start(services);
            }
        }

        QueryRecorder.SubscriptionOpened();
        return lease;
    }

    public void Leave(SubscriptionLease lease)
    {
        lock (gate)
        {
            leases = [.. leases.Where(_ => _ != lease)];
            if (lease.Caller is { } caller)
            {
                var held = callers.GetValueOrDefault(caller) - 1;
                if (held > 0)
                {
                    callers[caller] = held;
                }
                else
                {
                    callers.Remove(caller);
                }
            }

            if (leases.Length == 0)
            {
                Stop();
            }
        }

        QueryRecorder.SubscriptionClosed();
    }

    /// <summary>
    /// Waits for one of the places at the database. One write can make every live query due in the
    /// same instant; this is what makes that a queue rather than a stampede, and the first run of a
    /// reconnecting client waits in it like any other.
    /// </summary>
    public async ValueTask<IDisposable> RunSlot(Cancel cancel = default)
    {
        await slots.WaitAsync(cancel);
        return new Slot(slots);
    }

    // Called under the gate.
    void Start(IServiceProvider services)
    {
        // A host that never went through MapScry has not handed the backplane over yet. Resolving a
        // singleton from a scoped provider is fine, and the provider itself is not kept.
        changes.Attach(services);
        listening = changes.Listen(Offer);

        if (options.ChangeProbe is { } probe &&
            services.GetService<IServiceScopeFactory>() is { } scopes)
        {
            probing = new();
            _ = Probe(probe, scopes, probing.Token);
        }
    }

    // Called under the gate.
    void Stop()
    {
        listening?.Dispose();
        listening = null;
        probing?.Cancel();
        probing?.Dispose();
        probing = null;
    }

    // Runs on the thread that reported the change, inside the host's SaveChanges as often as not, so
    // it does no more than complete a signal — and that signal's continuations run elsewhere.
    void Offer(ScryChange change)
    {
        foreach (var lease in Volatile.Read(ref leases))
        {
            lease.Offer(change);
        }
    }

    void MarkAllDue()
    {
        foreach (var lease in Volatile.Read(ref leases))
        {
            lease.MarkDue();
        }
    }

    /// <summary>
    /// Asks the host's probe on a timer and makes every live query due when its answer moves. One loop
    /// for all of them, each question asked from a scope of its own.
    /// </summary>
    /// <remarks>
    /// Local only: what moved is the database, which every node probes for itself, so publishing it
    /// would have each node tell every other what all of them are about to find out. A probe that
    /// throws keeps its last answer and is counted; it never makes anything due, since a probe that
    /// fails every second would otherwise be a query storm of its own making.
    /// </remarks>
    async Task Probe(Func<IServiceProvider, Cancel, ValueTask<string?>> probe, IServiceScopeFactory scopes, Cancel cancel)
    {
        string? last = null;
        using var timer = new PeriodicTimer(options.ChangeProbeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancel))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (await probe(scope.ServiceProvider, cancel) is not { } token)
                    {
                        continue;
                    }

                    // The first answer has nothing to be compared with, and a write may have landed
                    // between a live query's first run and now — so it counts as having moved.
                    if (token != last)
                    {
                        MarkAllDue();
                    }

                    last = token;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    QueryRecorder.SignalFailed("probe", exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The last live query closed.
        }
    }

    sealed class Slot(SemaphoreSlim slots) :
        IDisposable
    {
        int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
