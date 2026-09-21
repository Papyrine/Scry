namespace Scry;

/// <summary>
/// Where a host says that data changed, so the live queries reading it are asked again. Registered as
/// a singleton by <c>AddScry</c> — and by <c>AddScryChanges</c> for a process that writes but serves
/// no queries — which is how a host reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScryChangeInterceptor"/> calls this for everything saved through a
/// <see cref="DbContext"/> it is attached to, and a cached policy's invalidation calls it for a grant.
/// What is left for a host to report is what neither can see: a bulk <c>ExecuteUpdate</c>, raw SQL, an
/// import, a POCO source's backing data.
/// </para>
/// <para>
/// Reporting a change never answers a query. It marks the live queries that read that entity as due,
/// and each is then run again through its policies — so a report that turns out to be wrong, or
/// arrives from somewhere it should not, costs a query and discloses nothing.
/// </para>
/// </remarks>
public sealed class ScryChanges
{
    Lock gate = new();
    Action<ScryChange>[] listeners = [];
    IScryChangeBackplane? backplane;
    IModel? model;
    IAsyncDisposable? inbound;
    Task reconciled = Task.CompletedTask;
    long backplaneFailures;

    internal ScryChanges()
    {
    }

    /// <summary>
    /// This node, as the others know it. Carried on every change raised here so that a backplane
    /// handing a node its own message back is recognised rather than acted on twice.
    /// </summary>
    public Guid Origin { get; } = Guid.NewGuid();

    /// <summary>Reports that rows of <typeparamref name="TEntity"/> changed.</summary>
    public void Notify<TEntity>() =>
        Notify(typeof(TEntity));

    /// <summary>
    /// Reports that rows of these types changed. A type deriving from a mapped one reports its root,
    /// so naming either reaches the live queries reading the other.
    /// </summary>
    public void Notify(params Type[] entities)
    {
        if (entities.Length == 0)
        {
            return;
        }

        // Which root a type's rows are read through is the model's to say. Before a model has been
        // seen there is no telling, and a report that might miss is worse than one that is too wide.
        if (model is not { } current)
        {
            NotifyAll();
            return;
        }

        Raise(
        [
            .. entities
                .SelectMany(_ => EntityNames.For(current, _))
                .Distinct(StringComparer.Ordinal)
        ]);
    }

    /// <summary>
    /// Reports that anything may have changed: every live query is asked again. What a writer that
    /// cannot say which entities it touched reports — a restored backup, a script run by hand.
    /// </summary>
    public void NotifyAll() =>
        Raise([]);

    /// <summary>How many times publishing to, or subscribing on, the backplane has failed.</summary>
    internal long BackplaneFailures => Interlocked.Read(ref backplaneFailures);

    /// <summary>Completes once the backplane subscription matches whether anything here is listening.</summary>
    internal Task Reconciled
    {
        get
        {
            lock (gate)
            {
                return reconciled;
            }
        }
    }

    internal void Raise(IReadOnlyList<string> names)
    {
        var change = new ScryChange(names, Origin);

        // Local listeners first and regardless: what happens to the backplane is never a reason for
        // this node's own live queries to miss this node's own write.
        Signal(change);

        if (backplane is { } plane)
        {
            _ = Publish(plane, change);
        }
    }

    // Never awaited and never thrown from: this runs inside the host's SaveChanges, where a backplane
    // that is down must cost the host's write nothing. The other nodes' poll is what covers the gap.
    async Task Publish(IScryChangeBackplane plane, ScryChange change)
    {
        try
        {
            await plane.PublishAsync(change, Cancel.None);
        }
        catch (Exception exception)
        {
            Failed(exception);
        }
    }

    /// <summary>
    /// Takes the backplane from the host's services, where one is registered. Safe to call more than
    /// once; the first backplane found is the one kept.
    /// </summary>
    internal ScryChanges Attach(IServiceProvider services)
    {
        if (backplane is not null ||
            services.GetService<IScryChangeBackplane>() is not { } plane)
        {
            return this;
        }

        lock (gate)
        {
            backplane ??= plane;
            Reconcile();
        }

        return this;
    }

    /// <summary>Takes the model that says which root a type's rows are read through.</summary>
    internal void Attach(IModel current) =>
        Interlocked.CompareExchange(ref model, current, null);

    /// <summary>
    /// Hands <paramref name="listener"/> every change, raised here or on another node, until the result
    /// is disposed. The listener runs on the thread that reported the change — inside the host's
    /// <c>SaveChanges</c>, often — so it must do no more than note that something is due.
    /// </summary>
    internal IDisposable Listen(Action<ScryChange> listener)
    {
        lock (gate)
        {
            listeners = [.. listeners, listener];
            Reconcile();
        }

        return new Listener(this, listener);
    }

    void Remove(Action<ScryChange> listener)
    {
        lock (gate)
        {
            listeners = [.. listeners.Where(_ => _ != listener)];
            Reconcile();
        }
    }

    void Signal(ScryChange change)
    {
        // A snapshot: the array is replaced rather than mutated, so this needs no lock and a listener
        // that leaves while it runs is at worst told once more.
        foreach (var listener in Volatile.Read(ref listeners))
        {
            try
            {
                listener(change);
            }
            catch (Exception)
            {
                // A listener is this library's own and only sets a flag. Should one ever throw, it
                // still must not surface in the write that reported the change.
            }
        }
    }

    // What another node published. This node's own messages come back too on most transports, and
    // were already acted on when they were raised.
    ValueTask Apply(ScryChange change, Cancel cancel)
    {
        if (change.Origin != Origin)
        {
            Signal(change);
        }

        return ValueTask.CompletedTask;
    }

    // Listening on the backplane is held only while something here wants to hear: a node with no live
    // query has nothing to re-ask. Publishing is unaffected — it needs no subscription. Chained rather
    // than run in place so that a subscribe still in flight is followed, never raced, by the
    // unsubscribe that a listener leaving asks for. Called under the gate.
    void Reconcile() =>
        reconciled = reconciled
            .ContinueWith(
                _ => ReconcileCore(),
                Cancel.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default)
            .Unwrap();

    async Task ReconcileCore()
    {
        try
        {
            var wanted = Volatile.Read(ref listeners).Length > 0;
            if (wanted &&
                inbound is null &&
                backplane is { } plane)
            {
                inbound = await plane.SubscribeAsync(Apply, Cancel.None);
                return;
            }

            if (!wanted &&
                inbound is { } current)
            {
                inbound = null;
                await current.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            Failed(exception);
        }
    }

    void Failed(Exception exception)
    {
        Interlocked.Increment(ref backplaneFailures);
        QueryRecorder.SignalFailed("backplane", exception);
    }

    sealed class Listener(ScryChanges owner, Action<ScryChange> listener) :
        IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Remove(listener);
            }
        }
    }
}
