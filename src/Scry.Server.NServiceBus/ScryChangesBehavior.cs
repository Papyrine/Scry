/// <summary>
/// Around the handling of each incoming message: gathers what its handlers save, and publishes it
/// once they are done. Registered by <c>UseScryChanges</c>.
/// </summary>
/// <remarks>
/// At the logical-message stage rather than around each handler, so that a message with two handlers
/// publishes one event for what both saved. A handler that throws leaves through here without
/// publishing, and NServiceBus discards whatever the attempt had sent — so a retry publishes once,
/// for the attempt that was kept.
/// </remarks>
sealed class ScryChangesBehavior :
    Behavior<IIncomingLogicalMessageContext>
{
    public override async Task Invoke(IIncomingLogicalMessageContext context, Func<Task> next)
    {
        // A change is never a reason to report a change: handling one saves nothing, and publishing
        // from here in answer to one would be a loop with a bus in it.
        if (context.Message.Instance is ScryChanged)
        {
            await next();
            return;
        }

        var handled = new HandledChanges();
        HandledChanges.Current = handled;
        try
        {
            await next();
        }
        finally
        {
            HandledChanges.Current = null;
        }

        if (handled.Take() is { } change)
        {
            await context.Publish(NServiceBusChangeBackplane.Event(change));
        }
    }
}
