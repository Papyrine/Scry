namespace Scry;

/// <summary>Wires MassTransit up as what carries commands from Scry servers to the consumers that handle them.</summary>
public static class ScryMassTransitExtensions
{
    // begin-snippet: useMassTransitCommands
    /// <summary>
    /// On a Scry server: publishes the commands <paramref name="configure"/> names over MassTransit,
    /// with their id and caller as headers, and finishes each when its consumer says how it ended.
    /// Claims nothing until told, since nothing about a MassTransit message says it is one.
    /// </summary>
    /// <remarks>
    /// The server's bus needs <see cref="AddScryCommandCompletions"/>, which is what hears the answers.
    /// </remarks>
    public static ScryOptions UseMassTransitCommands(this ScryOptions options, Action<BusCommands>? configure = null)
    {
        var claims = new BusCommands();
        configure?.Invoke(claims);
        options.AddDispatcher(_ => new MassTransitDispatcher(_, claims));
        return options;
    }

    /// <summary>
    /// On the server's bus: the consumer that hears how each command ended. On a temporary endpoint of
    /// its own, so that every node receives every completion — only the node that dispatched a command
    /// holds it, and one queue shared between nodes would hand each completion to one of them.
    /// </summary>
    public static IBusRegistrationConfigurator AddScryCommandCompletions(this IBusRegistrationConfigurator configurator)
    {
        configurator
            .AddConsumer<MassTransitCommandCompletedConsumer>()
            .Endpoint(_ => _.Temporary = true);
        return configurator;
    }

    /// <summary>
    /// On a worker's bus: publishes how each command a Scry server sent ended, once its consumers are
    /// done. Configure it before <c>UseMessageRetry</c>, so a failure is reported once retries are
    /// spent rather than at the first attempt.
    /// </summary>
    public static void UseScryCommands(this IBusFactoryConfigurator configurator, IRegistrationContext context) =>
        configurator.UseConsumeFilter(typeof(ScryCommandFilter<>), context);

    /// <summary>
    /// What a consumer of a command with a result answers with, published back to the server with the
    /// completion. Serialized as the server's own results are, so it arrives as the client expects it.
    /// </summary>
    public static void SetScryResult(this ConsumeContext context, object result)
    {
        if (!context.TryGetPayload<CommandResult>(out var holder))
        {
            throw new InvalidOperationException(
                "SetScryResult was called while consuming a message no Scry server sent as a command, or on a bus without UseScryCommands.");
        }

        holder.Json = JsonSerializer.Serialize(result, result.GetType(), ScryJson.Options);
    }
    // end-snippet
}
