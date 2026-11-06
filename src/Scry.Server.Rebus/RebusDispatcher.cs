/// <summary>
/// <see cref="ICommandDispatcher"/> over Rebus: sends the command the server bound, with its id and
/// caller as headers, routed by the bus's own routing.
/// </summary>
sealed class RebusDispatcher(IServiceProvider services, BusCommands claims) :
    ICommandDispatcher
{
    // Nothing by default: a Rebus message is any class, so nothing about one says it is a command
    // meant for the bus rather than for an in-process handler.
    public bool CanDispatch(Type command) =>
        claims.Claims(command);

    public Task Dispatch(CommandEnvelope envelope, Cancel cancel)
    {
        var headers = new Dictionary<string, string>
        {
            [ScryCommandHeaders.CommandId] = envelope.Id.ToString("D")
        };
        if (envelope.Caller is { } caller)
        {
            headers[ScryCommandHeaders.Caller] = caller;
        }

        return services
            .GetRequiredService<IBus>()
            .Send(envelope.Command, headers);
    }
}
