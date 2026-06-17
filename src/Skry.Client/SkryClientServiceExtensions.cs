using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Skry.Client;

/// <summary>Registration helpers for the Skry client.</summary>
public static class SkryClientServiceExtensions
{
    /// <summary>
    /// Registers a <see cref="SkryClient"/> that POSTs to <paramref name="endpoint"/> using the
    /// registered <see cref="HttpClient"/>. Register your generated query entry point separately.
    /// </summary>
    public static IServiceCollection AddSkryClient(this IServiceCollection services, string endpoint)
    {
        services.AddScoped(provider => SkryClient.ForHttp(provider.GetRequiredService<HttpClient>(), endpoint));
        return services;
    }
}
