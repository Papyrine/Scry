/// <summary>
/// <see cref="ICommandDispatcher"/> over Wolverine: sends the command the server bound, with its id and
/// caller as headers, routed by the application's own routing.
/// </summary>
sealed class WolverineDispatcher(IServiceProvider services, BusCommands claims) :
    ICommandDispatcher
{
    // Nothing by default: a Wolverine message is any class, so nothing about one says it is a command
    // meant for the bus rather than for an in-process handler.
    public bool CanDispatch(Type command) =>
        claims.Claims(command);

    public async Task Dispatch(CommandEnvelope envelope, Cancel cancel)
    {
        var options = new DeliveryOptions().WithHeader(ScryCommandHeaders.CommandId, envelope.Id.ToString("D"));
        if (envelope.Caller is { } caller)
        {
            options = options.WithHeader(ScryCommandHeaders.Caller, caller);
        }

        // A bus of its own, as a request's would be: the one Wolverine registers is scoped.
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IMessageBus>()
            .SendAsync(envelope.Command, options);
    }
}
