namespace Scry;

/// <summary>Wires MessagePipe's distributed pub/sub up as the backplane that carries changes between nodes.</summary>
public static class ScryMessagePipeExtensions
{
    // begin-snippet: useMessagePipeBackplane
    /// <summary>
    /// Carries this node's changes to the deployment's other nodes over MessagePipe, and theirs to
    /// this one, so a write on any of them re-asks the live queries held by all.
    /// </summary>
    /// <remarks>
    /// The host registers MessagePipe and a distributed transport for it as it would for anything
    /// else — <c>AddMessagePipe().AddRedis(…)</c>, NATS, an interprocess pipe — and this publishes
    /// and subscribes through whichever that is. One adapter, and every transport MessagePipe has.
    /// </remarks>
    public static ScryOptions UseMessagePipeBackplane(this ScryOptions options, Action<ScryMessagePipeOptions>? configure = null)
    {
        var pipe = new ScryMessagePipeOptions();
        configure?.Invoke(pipe);
        options.UseBackplane(_ => Create(_, pipe));
        return options;
    }
    // end-snippet

    /// <summary>
    /// The same, for a process that writes but serves no queries and so has no <c>AddScry</c> to
    /// configure: registered beside <c>AddScryChanges</c>, it is what carries what that process saves
    /// to the nodes whose live queries read it.
    /// </summary>
    public static IServiceCollection AddScryMessagePipeBackplane(this IServiceCollection services, Action<ScryMessagePipeOptions>? configure = null)
    {
        var pipe = new ScryMessagePipeOptions();
        configure?.Invoke(pipe);
        services.TryAddSingleton<IScryChangeBackplane>(_ => Create(_, pipe));
        return services;
    }

    static MessagePipeChangeBackplane Create(IServiceProvider services, ScryMessagePipeOptions options) =>
        new(
            services.GetRequiredService<IDistributedPublisher<string, string>>(),
            services.GetRequiredService<IDistributedSubscriber<string, string>>(),
            options);
}
