namespace Scry;

/// <summary>Wires Redis pub/sub up as the backplane that carries changes between nodes.</summary>
public static class ScryRedisExtensions
{
    // begin-snippet: useRedisBackplane
    /// <summary>
    /// Carries this node's changes to the deployment's other nodes over Redis, and theirs to this
    /// one, so a write on any of them re-asks the live queries held by all. The
    /// <see cref="IConnectionMultiplexer"/> is taken from the host's services.
    /// </summary>
    /// <remarks>
    /// For a deployment of several nodes whose database cannot say when it was last written. One
    /// that can — SQL Server and PostgreSQL, through Scry.Server.Delta's <c>UseDeltaChanges</c> —
    /// needs no backplane: the database is one. The two compose, and a backplane says which entities
    /// changed where the database's marker cannot.
    /// </remarks>
    public static ScryOptions UseRedisBackplane(this ScryOptions options, Action<ScryRedisOptions>? configure = null)
    {
        var redis = new ScryRedisOptions();
        configure?.Invoke(redis);
        options.UseBackplane(_ => Create(_, redis));
        return options;
    }
    // end-snippet

    /// <summary>
    /// The same, for a process that writes but serves no queries and so has no <c>AddScry</c> to
    /// configure: registered beside <c>AddScryChanges</c>, it is what carries what that process saves
    /// to the nodes whose live queries read it.
    /// </summary>
    public static IServiceCollection AddScryRedisBackplane(this IServiceCollection services, Action<ScryRedisOptions>? configure = null)
    {
        var redis = new ScryRedisOptions();
        configure?.Invoke(redis);
        services.TryAddSingleton<IScryChangeBackplane>(_ => Create(_, redis));
        return services;
    }

    static RedisChangeBackplane Create(IServiceProvider services, ScryRedisOptions options) =>
        new(new RedisPubSub(services.GetRequiredService<IConnectionMultiplexer>()), options);
}
