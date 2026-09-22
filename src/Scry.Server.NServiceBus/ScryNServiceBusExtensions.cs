namespace Scry;

/// <summary>
/// Wires NServiceBus up as what carries changes to the Scry servers whose live queries read them, and
/// commands from those servers to the workers that handle them.
/// </summary>
public static class ScryNServiceBusExtensions
{
    // begin-snippet: useNServiceBusBackplane
    /// <summary>
    /// On a Scry server: hears the <see cref="ScryChanged"/> events other endpoints publish, and
    /// publishes this node's own changes as one. The endpoint has to be able to receive — a send-only
    /// endpoint hears nothing — and is found through the host's <see cref="IMessageSession"/>.
    /// </summary>
    public static ScryOptions UseNServiceBusBackplane(this ScryOptions options)
    {
        options.UseBackplane(Create, _ => _.TryAddSingleton<ChangeRelay>());
        return options;
    }
    // end-snippet

    // begin-snippet: addScryNServiceBusBackplane
    /// <summary>
    /// On an endpoint that writes the data a Scry server reads but serves no queries itself — a
    /// worker. With <see cref="ScryChangeInterceptor"/> on its contexts and
    /// <see cref="UseScryChanges"/> on its endpoint, what its handlers save reaches the live queries
    /// held by the servers.
    /// </summary>
    public static IServiceCollection AddScryNServiceBusBackplane(this IServiceCollection services)
    {
        services.AddScryChanges();
        services.TryAddSingleton<ChangeRelay>();
        services.TryAddSingleton<IScryChangeBackplane>(Create);
        return services;
    }

    /// <summary>
    /// On that endpoint's configuration: publishes what each incoming message's handlers saved, once,
    /// after they are done and through that message's own context — so the event leaves with the
    /// rest of what the handler sent, and only if the handler's work was kept.
    /// </summary>
    /// <remarks>
    /// Also stops the endpoint subscribing to <see cref="ScryChanged"/> itself. It publishes them and
    /// holds no live query to re-ask, so receiving every other endpoint's would be traffic for nothing.
    /// </remarks>
    public static EndpointConfiguration UseScryChanges(this EndpointConfiguration configuration)
    {
        configuration.Pipeline.Register(
            new ScryChangesBehavior(),
            "Publishes what each message's handlers saved as a ScryChanged event, for Scry live queries.");
        configuration.AutoSubscribe().DisableFor<ScryChanged>();
        return configuration;
    }
    // end-snippet

    // begin-snippet: useNServiceBusCommands
    /// <summary>
    /// On a Scry server: sends the commands it claims over NServiceBus, through the host's
    /// <see cref="IMessageSession"/> and routed by the endpoint's own routing, and finishes each when
    /// the worker that handled it replies. Claims every command the endpoint knows as an NServiceBus
    /// <see cref="ICommand"/> by marker, and those <paramref name="configure"/> names — never the rest,
    /// which stay with their in-process handlers.
    /// </summary>
    /// <remarks>
    /// The endpoint has to be able to receive, since the reply comes back to it — and to this node, so
    /// each node is an endpoint of its own, as the change backplane already needs.
    /// </remarks>
    public static ScryOptions UseNServiceBusCommands(this ScryOptions options, Action<BusCommands>? configure = null)
    {
        var claims = new BusCommands();
        configure?.Invoke(claims);
        options.AddDispatcher(_ => new NServiceBusDispatcher(_, claims));
        return options;
    }

    /// <summary>
    /// On an endpoint whose handlers handle commands a Scry server sends: replies to each with how it
    /// ended, once its handlers are done and through the message's own context — so the reply leaves
    /// only if the handlers' work is kept, and once for a message that was retried. A message that
    /// exhausts recoverability is answered as failed as it goes to the error queue.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="UseScryChanges"/>: a worker may reply without publishing changes, where the
    /// servers hear of what it saved some other way. A command's own target is re-asked on completion
    /// either way.
    /// </remarks>
    public static EndpointConfiguration UseScryCommands(this EndpointConfiguration configuration)
    {
        var failed = new FailedCommands();
        configuration.GetSettings().Set(failed);
        configuration.EnableFeature<ScryCommandsFeature>();
        configuration.Pipeline.Register(
            new ScryCommandsBehavior(),
            "Replies to each command a Scry server sent with how it ended.");
        configuration.Recoverability().Failed(_ => _.OnMessageSentToErrorQueue(failed.Answer));
        return configuration;
    }

    /// <summary>
    /// What a handler of a command with a result answers with, sent back to the server in the reply.
    /// Serialized as the server's own results are, so it arrives as the client expects it.
    /// </summary>
    public static void SetScryResult(this IMessageHandlerContext context, object result)
    {
        if (!context.Extensions.TryGet<CommandResult>(out var holder))
        {
            throw new InvalidOperationException(
                "SetScryResult was called while handling a message no Scry server sent as a command, or on an endpoint without UseScryCommands.");
        }

        holder.Json = JsonSerializer.Serialize(result, result.GetType(), ScryJson.Options);
    }
    // end-snippet

    static NServiceBusChangeBackplane Create(IServiceProvider services) =>
        new(services, services.GetRequiredService<ChangeRelay>());
}
