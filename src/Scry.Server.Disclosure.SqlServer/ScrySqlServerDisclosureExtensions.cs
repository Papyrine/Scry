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

                // Only where there is a chain to check. With ledger tables the checking is the
                // database's own, and a check of no chain would read as one that held.
                if (settings.HashChain)
                {
                    services.TryAddSingleton<IScryDisclosureVerifier>(kept);
                }

                services.AddHostedService(_ => new DisclosureDrainHost(kept));
            },
            configure);
        return options;
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
