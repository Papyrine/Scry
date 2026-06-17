namespace Scry.Client;

/// <summary>Registration helpers for the Scry client.</summary>
public static class ScryClientServiceExtensions
{
    /// <summary>
    /// Registers a <see cref="ScryClient"/> that POSTs to <paramref name="endpoint"/> using the
    /// registered <see cref="HttpClient"/>. Register your generated query entry point separately.
    /// </summary>
    public static IServiceCollection AddScryClient(this IServiceCollection services, string endpoint)
    {
        services.AddScoped(provider => ScryClient.ForHttp(provider.GetRequiredService<HttpClient>(), endpoint));
        return services;
    }
}
