namespace Scry;

/// <summary>Wires Rebus up as what carries commands from Scry servers to the handlers that handle them.</summary>
public static class ScryRebusExtensions
{
    // begin-snippet: useRebusCommands
    /// <summary>
    /// On a Scry server: sends the commands <paramref name="configure"/> names over Rebus, with their id
    /// and caller as headers and routed by the bus's own routing, and finishes each when the reply to
    /// it arrives. Claims nothing until told, since nothing about a Rebus message says it is one.
    /// </summary>
    /// <remarks>
    /// The server's bus needs <see cref="RebusCommandCompletedHandler"/> registered as a handler, and
    /// an input queue of its own on each node, since that is where the reply comes back to.
    /// </remarks>
    public static ScryOptions UseRebusCommands(this ScryOptions options, Action<BusCommands>? configure = null)
    {
        var claims = new BusCommands();
        configure?.Invoke(claims);
        options.AddDispatcher(_ => new RebusDispatcher(_, claims));
        return options;
    }

    /// <summary>
    /// On a worker's bus: replies to each message a Scry server sent as a command with how it ended —
    /// once its handlers have returned, inside the message's own transaction, so the reply leaves only
    /// if their work is kept. A message that exhausts its delivery attempts is answered as failed as it
    /// goes to the error queue.
    /// </summary>
    public static OptionsConfigurer EnableScryCompletion(this OptionsConfigurer options)
    {
        options.Register(_ => new CompletionSender(_.Get<ISerializer>(), _.Get<ITransport>(), _.Get<IMessageTypeNameConvention>()));
        options.Decorate<IPipeline>(
            _ => new PipelineStepInjector(_.Get<IPipeline>())
                .OnReceive(new CompletionStep(_.Get<CompletionSender>()), PipelineRelativePosition.After, typeof(DispatchIncomingMessageStep)));
        options.Decorate<IErrorHandler>(_ => new CompletionErrorHandler(_.Get<IErrorHandler>(), _.Get<CompletionSender>()));
        return options;
    }

    /// <summary>
    /// What a handler of a command with a result answers with, sent back to the server in the reply.
    /// Serialized as the server's own results are, so it arrives as the client expects it.
    /// </summary>
    public static void SetScryResult(this IMessageContext context, object result)
    {
        if (!context.Headers.ContainsKey(ScryCommandHeaders.CommandId))
        {
            throw new InvalidOperationException("SetScryResult was called while handling a message no Scry server sent as a command.");
        }

        var step = context.IncomingStepContext;
        var holder = step.Load<CommandResult>();
        if (holder is null)
        {
            holder = new();
            step.Save(holder);
        }

        holder.Json = JsonSerializer.Serialize(result, result.GetType(), ScryJson.Options);
    }
    // end-snippet
}
