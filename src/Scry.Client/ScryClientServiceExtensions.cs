namespace Scry;

/// <summary>Registration helpers for the Scry client.</summary>
public static class ScryClientServiceExtensions
{
    /// <summary>
    /// Registers a <see cref="ScryClient"/> that POSTs to <paramref name="endpoint"/> using the
    /// <see cref="HttpClient"/> registered in the container. Register your generated query entry point
    /// separately.
    /// </summary>
    /// <remarks>
    /// This takes whichever <see cref="HttpClient"/> the container happens to hold, which is what a
    /// Blazor WebAssembly app wants: there is exactly one, it points at the app's own origin, and it is
    /// backed by the browser rather than by a socket pool. Anywhere else a bare <see cref="HttpClient"/>
    /// registration is discouraged to begin with, and an ambient one may well belong to some other API —
    /// so use the overload that names the client Scry should use.
    /// </remarks>
    public static IServiceCollection AddScryClient(this IServiceCollection services, string endpoint)
    {
        services.AddScoped(_ => ScryClient.ForHttp(_.GetRequiredService<HttpClient>(), endpoint));
        return services;
    }

    /// <summary>
    /// Registers a <see cref="ScryClient"/> that POSTs to <paramref name="endpoint"/> using the
    /// <see cref="HttpClient"/> that <paramref name="httpClient"/> resolves — typically a named client
    /// from <c>IHttpClientFactory</c>.
    /// </summary>
    /// <remarks>
    /// Naming the client keeps Scry's configuration — its base address, and any handler pipeline it
    /// needs — separate from every other HTTP call the application makes, and hands its handler
    /// lifetime to the factory. The factory is reached through the delegate rather than being a
    /// dependency of this package, so an application that does not otherwise want
    /// <c>Microsoft.Extensions.Http</c> does not acquire it by referencing Scry.
    /// <para>
    /// The client is registered scoped rather than transient. It records the schema stamp each response
    /// advertises and raises <see cref="ScryClient.SchemaStaleDetected"/> at most once, so a fresh
    /// instance per injection would reset that and never report drift. That is also why a typed client
    /// (<c>AddHttpClient&lt;ScryClient&gt;</c>) is the wrong shape here: the factory registers those
    /// transient.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddScryClient(
        this IServiceCollection services,
        string endpoint,
        Func<IServiceProvider, HttpClient> httpClient)
    {
        services.AddScoped(_ => ScryClient.ForHttp(httpClient(_), endpoint));
        return services;
    }
}
