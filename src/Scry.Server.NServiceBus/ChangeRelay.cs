/// <summary>
/// Where the handler NServiceBus constructs hands what it received to the backplane Scry constructed.
/// One per container, which is one per endpoint: the two are built by different things and have only
/// the container in common.
/// </summary>
sealed class ChangeRelay
{
    Lock gate = new();
    Func<ScryChange, Cancel, ValueTask>[] handlers = [];
    long dropped;

    /// <summary>How many events carried something that was not a change, and were ignored.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    public IAsyncDisposable Add(Func<ScryChange, Cancel, ValueTask> handler)
    {
        lock (gate)
        {
            handlers = [.. handlers, handler];
        }

        return new Registration(this, handler);
    }

    public async Task Deliver(string payload, Cancel cancel)
    {
        if (!ScryChange.TryParse(payload, out var change))
        {
            Interlocked.Increment(ref dropped);
            return;
        }

        // A snapshot: the array is replaced rather than mutated. With nothing listening — an
        // endpoint holding no live query just now — the event is received and that is all.
        foreach (var handler in Volatile.Read(ref handlers))
        {
            await handler(change, cancel);
        }
    }

    void Remove(Func<ScryChange, Cancel, ValueTask> handler)
    {
        lock (gate)
        {
            handlers = [.. handlers.Where(_ => _ != handler)];
        }
    }

    sealed class Registration(ChangeRelay owner, Func<ScryChange, Cancel, ValueTask> handler) :
        IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Remove(handler);
            return ValueTask.CompletedTask;
        }
    }
}
