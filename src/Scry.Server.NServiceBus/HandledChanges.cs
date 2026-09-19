/// <summary>
/// What one incoming message's handlers saved, gathered while they run so that it can be published
/// once, afterwards, through that message's own context.
/// </summary>
/// <remarks>
/// <para>
/// Through the context, and not through the message session a backplane otherwise publishes with,
/// because of when the event then leaves: with the rest of what the handler sent, only once the
/// handler's work is kept. With the outbox that is after its transaction commits; without it, after
/// the handlers return without throwing. A handler that fails publishes nothing, and one that is
/// retried publishes once for the attempt that succeeded — which is what "data changed" should mean.
/// </para>
/// <para>
/// Found through an <see cref="AsyncLocal{T}"/> because the two ends are far apart and neither can be
/// handed to the other: one is a pipeline behavior, the other is reached from inside
/// <c>SaveChanges</c> by way of an interceptor. It flows because nothing between them leaves the
/// handler's own asynchronous flow before the change is added.
/// </para>
/// </remarks>
sealed class HandledChanges
{
    static AsyncLocal<HandledChanges?> current = new();

    HashSet<string> entities = new(StringComparer.Ordinal);
    bool everything;
    bool any;
    Guid origin;

    public static HandledChanges? Current
    {
        get => current.Value;
        set => current.Value = value;
    }

    public void Add(ScryChange change)
    {
        lock (entities)
        {
            any = true;
            origin = change.Origin;
            everything |= change.Everything;
            entities.UnionWith(change.Entities);
        }
    }

    /// <summary>Everything gathered, as one change, or null where nothing was saved.</summary>
    public ScryChange? Take()
    {
        lock (entities)
        {
            if (!any)
            {
                return null;
            }

            // "Anything may have changed" is what an empty list says, and saying it beats naming
            // some of what changed beside something that could not be named.
            return new(everything ? [] : [.. entities], origin);
        }
    }
}
