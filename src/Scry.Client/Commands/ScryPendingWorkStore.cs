namespace Scry;

/// <summary>
/// The commands a client stopped waiting for while the server was still handling them: each one from
/// the moment it went pending until its outcome has been seen. What a pending-work panel lists. One per
/// <see cref="ScryClient"/>, as <see cref="ScryClient.PendingWork"/>.
/// </summary>
/// <remarks>
/// A command the server decides within <see cref="ScryClient.CommandWait"/> never appears here: its
/// sender has its outcome in hand. A completed one leaves on its own after
/// <see cref="CompletedLinger"/>; a failed or unknown one stays until <see cref="ClearFinished"/>,
/// since it is saying something somebody should read.
/// </remarks>
public sealed class ScryPendingWorkStore
{
    Lock sync = new();
    List<(ScryPendingCommand Item, SynchronizationContext? Context)> items = [];

    /// <summary>
    /// How long a completed command stays listed after its outcome arrives, so that it is seen to
    /// finish rather than vanishing. Eight seconds unless set. Zero drops it as it completes.
    /// </summary>
    public TimeSpan CompletedLinger { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>A snapshot of what is listed, oldest first.</summary>
    public IReadOnlyList<ScryPendingCommand> Items
    {
        get
        {
            lock (sync)
            {
                return [.. items.Select(_ => _.Item)];
            }
        }
    }

    /// <summary>How many of the listed commands are still pending.</summary>
    public int PendingCount
    {
        get
        {
            lock (sync)
            {
                return items.Count(_ => _.Item.Status == ScryCommandStatus.Pending);
            }
        }
    }

    /// <summary>
    /// Raised after a command is listed, changes, or leaves. Raised on the
    /// <see cref="SynchronizationContext"/> the command was sent on, where there was one — so a UI
    /// component can re-render from the handler without marshalling — and otherwise where the change
    /// happened.
    /// </summary>
    public event Action? Changed;

    /// <summary>Drops every command that is no longer pending, however it finished.</summary>
    public void ClearFinished()
    {
        lock (sync)
        {
            items.RemoveAll(_ => _.Item.Status != ScryCommandStatus.Pending);
        }

        Raise();
    }

    internal void Add(ScryPendingCommand item, SynchronizationContext? context)
    {
        lock (sync)
        {
            items.Add((item, context));
        }

        Post(context, Raise);
    }

    // The item changes where it is read — on the context it was sent from — so a component rendering
    // from Changed sees it whole.
    internal void Finish(ScryPendingCommand item, ScryCommandOutcome outcome)
    {
        var context = ContextOf(item);
        Post(
            context,
            () =>
            {
                item.Status = outcome.Status;
                item.Error = outcome.Error;
                item.Finished = DateTimeOffset.Now;
                Raise();
                if (outcome.Status == ScryCommandStatus.Completed)
                {
                    _ = Linger(item, context);
                }
            });
    }

    async Task Linger(ScryPendingCommand item, SynchronizationContext? context)
    {
        var linger = CompletedLinger;
        if (linger > TimeSpan.Zero)
        {
            await Task.Delay(linger).ConfigureAwait(false);
        }

        bool removed;
        lock (sync)
        {
            removed = items.RemoveAll(_ => ReferenceEquals(_.Item, item)) > 0;
        }

        if (removed)
        {
            Post(context, Raise);
        }
    }

    SynchronizationContext? ContextOf(ScryPendingCommand item)
    {
        lock (sync)
        {
            return items.FirstOrDefault(_ => ReferenceEquals(_.Item, item)).Context;
        }
    }

    static void Post(SynchronizationContext? context, Action action)
    {
        if (context is null)
        {
            action();
            return;
        }

        context.Post(_ => action(), null);
    }

    // A panel that throws while redrawing must not stop the store recording what happened.
    void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Nothing useful to do about it here.
        }
    }
}
