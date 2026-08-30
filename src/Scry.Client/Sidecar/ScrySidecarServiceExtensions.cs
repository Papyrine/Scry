namespace Scry;

/// <summary>Registration for the debug sidecar.</summary>
public static class ScrySidecarServiceExtensions
{
    /// <summary>
    /// Registers the sidecar's store, options, and capture handler. Two things remain for the app:
    /// attach <see cref="ScrySidecarHandler"/> to the client Scry uses
    /// (<c>services.AddHttpClient("scry").AddHttpMessageHandler&lt;ScrySidecarHandler&gt;()</c>),
    /// and render <see cref="ScrySidecar"/> once, above the router.
    /// </summary>
    /// <remarks>
    /// The handler is attached by the app rather than here so this package does not acquire
    /// <c>Microsoft.Extensions.Http</c> — and so the sidecar observes exactly the client the app
    /// points it at, not every <see cref="HttpClient"/> in the container. Register it after any
    /// caching handler, so what it records is the real wire exchange rather than a replay.
    /// </remarks>
    public static IServiceCollection AddScrySidecar(
        this IServiceCollection services,
        Action<ScrySidecarOptions>? configure = null)
    {
        var options = new ScrySidecarOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<ScrySidecarStore>();
        services.AddTransient<ScrySidecarHandler>();
        return services;
    }
}
