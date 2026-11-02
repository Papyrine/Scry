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
    /// On the server's bus: the consumers that hear how each command ended — the completion a worker
    /// publishes when its consumer succeeds, and the fault MassTransit publishes once it has given up
    /// on one. On temporary endpoints of their own, so that every node receives every answer — only
    /// the node that dispatched a command holds it, and one queue shared between nodes would hand each
    /// answer to one of them.
    /// </summary>
    /// <remarks>
    /// A fault is how a failure is told, so a worker's bus must publish them, which it does unless
    /// told not to. One that does not leaves a failed command to be failed by the server's own
    /// retention, as "No completion was received", when <c>CommandRetention</c> has passed.
    /// </remarks>
    public static IBusRegistrationConfigurator AddScryCommandCompletions(this IBusRegistrationConfigurator configurator)
    {
        configurator
            .AddConsumer<MassTransitCommandCompletedConsumer>()
            .Endpoint(_ => _.Temporary = true);
        configurator
            .AddConsumer<MassTransitCommandFaultConsumer>()
            .Endpoint(_ => _.Temporary = true);
        return configurator;
    }

    /// <summary>
    /// On a worker's bus: publishes that each command a Scry server sent succeeded, once its consumers
    /// are done. A failure is the fault MassTransit publishes once retries and redeliveries are spent,
    /// so a retry that recovers from a failed attempt completes the command.
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
