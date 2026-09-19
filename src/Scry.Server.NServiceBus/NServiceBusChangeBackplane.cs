/// <summary>
/// <see cref="IScryChangeBackplane"/> over NServiceBus: a change is a <see cref="ScryChanged"/> event,
/// published by whoever wrote and handled by every endpoint that serves live queries.
/// </summary>
/// <remarks>
/// NServiceBus delivers an event to one instance of each logical endpoint — instances of an endpoint
/// compete for its queue. So this reaches every Scry server only where each is an endpoint of its own,
/// which for scaled-out web nodes means an endpoint name per instance. Where that is not wanted, this
/// still does the job it is most useful for — a worker's writes reaching the web tier — and the
/// database's change marker can be what carries changes between the web nodes themselves.
/// </remarks>
sealed class NServiceBusChangeBackplane(IServiceProvider services, ChangeRelay relay) :
    IScryChangeBackplane
{
    public async ValueTask PublishAsync(ScryChange change, Cancel cancel)
    {
        // Saved by a message handler: held, and published when the handler is done, through the
        // message's own context. Added before anything is awaited, while this is still running inside
        // the save that reported it.
        if (HandledChanges.Current is { } handled)
        {
            handled.Add(change);
            return;
        }

        // Saved by anything else — a web request, a background job — and published at once. Resolved
        // when first needed rather than when this is built: the session exists once the endpoint has
        // started, which is after the container has.
        await services
            .GetRequiredService<IMessageSession>()
            .Publish(Event(change), cancel);
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(Func<ScryChange, Cancel, ValueTask> handler, Cancel cancel) =>
        new(relay.Add(handler));

    public static ScryChanged Event(ScryChange change) =>
        new()
        {
            Payload = change.Serialize()
        };
}
