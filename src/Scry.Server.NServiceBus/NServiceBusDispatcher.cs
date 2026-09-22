/// <summary>
/// <see cref="ICommandDispatcher"/> over NServiceBus: sends the command the server bound, through the
/// host's <see cref="IMessageSession"/>, routed by the endpoint's own routing. The worker that handles
/// it replies with <see cref="ScryCommandCompleted"/>, which finishes it here.
/// </summary>
/// <remarks>
/// The reply comes back to this endpoint, so the node that dispatched a command is the one that hears
/// how it ended — which needs an endpoint per node, as the change backplane already does, or
/// <c>MakeInstanceUniquelyAddressable</c> where nodes share a name.
/// </remarks>
sealed class NServiceBusDispatcher(IServiceProvider services, BusCommands claims) :
    ICommandDispatcher
{
    // A message the endpoint already calls a command by marker. Not one it calls a command by
    // convention: that is the endpoint's to say, and it says it by naming the command here.
    public bool CanDispatch(Type command) =>
        claims.Claims(command, typeof(ICommand).IsAssignableFrom);

    public async Task Dispatch(CommandEnvelope envelope, Cancel cancel)
    {
        var options = new SendOptions();
        options.SetHeader(ScryCommandHeaders.CommandId, envelope.Id.ToString("D"));
        if (envelope.Caller is { } caller)
        {
            options.SetHeader(ScryCommandHeaders.Caller, caller);
        }

        // Resolved when first needed rather than when this is built: the session exists once the
        // endpoint has started, which is after the container has.
        await services
            .GetRequiredService<IMessageSession>()
            .Send(envelope.Command, options, cancel);
    }
}
