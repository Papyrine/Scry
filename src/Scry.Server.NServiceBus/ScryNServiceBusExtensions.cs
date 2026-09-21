namespace Scry;

/// <summary>Wires NServiceBus up as what carries changes to the Scry servers whose live queries read them.</summary>
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

    static NServiceBusChangeBackplane Create(IServiceProvider services) =>
        new(services, services.GetRequiredService<ChangeRelay>());
}
