namespace Scry;

/// <summary>
/// Receives <see cref="ScryChanged"/> on an endpoint that serves live queries, and hands it on. Found
/// by NServiceBus's assembly scanning like any other handler, which is what subscribes the endpoint
/// to the event.
/// </summary>
/// <remarks>
/// Public because NServiceBus constructs it. It does nothing a host would call.
/// </remarks>
public sealed class ScryChangedHandler(IServiceProvider services) :
    IHandleMessages<ScryChanged>
{
    /// <inheritdoc />
    public Task Handle(ScryChanged message, IMessageHandlerContext context)
    {
        // Absent on an endpoint that scanned this assembly and registered no backplane: a worker
        // that publishes changes and has no live queries of its own to re-ask.
        if (services.GetService<ChangeRelay>() is not { } relay)
        {
            return Task.CompletedTask;
        }

        return relay.Deliver(message.Payload, context.CancellationToken);
    }
}
