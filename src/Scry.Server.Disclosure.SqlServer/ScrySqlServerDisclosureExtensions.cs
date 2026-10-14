namespace Scry;

/// <summary>Wires SQL Server up as where the disclosure audit is kept.</summary>
public static class ScrySqlServerDisclosureExtensions
{
    // begin-snippet: useSqlServerDisclosureAudit
    /// <summary>
    /// Turns the disclosure audit on and keeps it in SQL Server: every answer is accepted into an
    /// outbox table in <paramref name="connectionString"/>'s database before it is sent.
    /// </summary>
    /// <param name="options">The server's options.</param>
    /// <param name="connectionString">
    /// The database to keep the record in. The application's own is the usual choice, since an
    /// answer then waits on nothing the request did not already depend on.
    /// </param>
    /// <param name="store">The schema the tables live in, whether to create them, and how long to wait.</param>
    /// <param name="configure">The audit's own settings: who the caller is, the address key, a journal.</param>
    public static ScryOptions UseSqlServerDisclosureAudit(
        this ScryOptions options,
        string connectionString,
        Action<ScrySqlServerDisclosureOptions>? store = null,
        Action<ScryDisclosureOptions>? configure = null)
    {
        var settings = new ScrySqlServerDisclosureOptions
        {
            ConnectionString = connectionString
        };
        store?.Invoke(settings);

        // Made here rather than by the container, so that a processor built by hand — which has no
        // container — records through the same store one built by AddScry does. It holds no
        // connection until it is used.
        var kept = new ScrySqlServerDisclosureStore(settings);
        options.UseDisclosureAudit(
            _ => _.GetService<ScrySqlServerDisclosureStore>() ?? kept,
            services =>
            {
                // The store is the sink, and also what reads the record back, erases from it and
                // says how it stands: whatever asks the container for any of those is handed it.
                services.TryAddSingleton(kept);
                services.TryAddSingleton<IScryDisclosureReader>(kept);
                services.TryAddSingleton<IScryDisclosureEraser>(kept);
                services.TryAddSingleton<IScryDisclosureStatus>(kept);

                services.TryAddSingleton<IScryDisclosureVerifier>(kept);

                services.AddHostedService(_ => new DisclosureDrainHost(kept));
            },
            configure);
        return options;
    }
    // end-snippet

    // begin-snippet: addSqlServerDisclosureRecord
    /// <summary>
    /// Registers the record SQL Server keeps for a host that reads it and serves no queries: an admin
    /// application with the disclosure explorer on it, apart from the servers that answer. Needs no
    /// model and no <c>AddScry</c>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="connectionString">The database the serving nodes keep the record in.</param>
    /// <param name="store">The schema, the timeout and where content is kept: as the serving nodes have them.</param>
    /// <param name="configure">
    /// The audit's settings. The same lambda the serving nodes are configured with can be passed, so
    /// that the address key is the same.
    /// </param>
    /// <remarks>
    /// <para>
    /// This host does not move the record on. What a reviewer asks here is accepted into the outbox
    /// before it is answered, as an answer is on a serving node, and is moved into the record by the
    /// serving nodes: so a reviewer's own questions show in the record a moment after they are asked.
    /// Starting segments and retiring them is theirs too, by their settings, so one host cannot roll
    /// the record on a schedule of its own. Those settings are refused here rather than ignored.
    /// </para>
    /// <para>
    /// Erasing is the one thing done here that moves the record on: what is waiting is moved first,
    /// so that an answer accepted a moment ago is erased with the rest. It starts no segment and
    /// retires none.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSqlServerDisclosureRecord(
        this IServiceCollection services,
        string connectionString,
        Action<ScrySqlServerDisclosureOptions>? store = null,
        Action<ScryDisclosureOptions>? configure = null)
    {
        var settings = new ScrySqlServerDisclosureOptions
        {
            ConnectionString = connectionString
        };
        store?.Invoke(settings);
        if (settings.RollAfter is not null ||
            settings.RollAtBytes is not null ||
            settings.Keep is not null ||
            settings.Retire is not null)
        {
            throw new ArgumentException(
                "A host that only reads the disclosure record does not move it on, " +
                "so it neither starts segments nor retires them: RollAfter, RollAtBytes, " +
                "Keep and Retire belong on the serving nodes, which are what move it.");
        }

        settings.DrainInterval = null;
        var kept = new ScrySqlServerDisclosureStore(settings);
        services.TryAddSingleton(kept);
        services.AddScryDisclosureRecord(kept, configure);
        return services;
    }
    // end-snippet
}

/// <summary>
/// Starts the store moving accepted batches on when the host starts, and stops it when the host
/// does. Without it the store starts itself on the first answer it accepts, which leaves a node that
/// has answered nothing yet moving nothing — including what it left in the outbox when it last stopped.
/// </summary>
sealed class DisclosureDrainHost(ScrySqlServerDisclosureStore store) :
    IHostedService
{
    public Task StartAsync(Cancel cancel)
    {
        store.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(Cancel cancel) =>
        store.DisposeAsync().AsTask();
}
