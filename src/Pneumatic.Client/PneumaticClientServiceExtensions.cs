using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Pneumatic.Client;

/// <summary>Registration helpers for the Pneumatic client.</summary>
public static class PneumaticClientServiceExtensions
{
    /// <summary>
    /// Registers a <see cref="PneumaticClient"/> that POSTs to <paramref name="endpoint"/> using the
    /// registered <see cref="HttpClient"/>. Register your generated query entry point separately.
    /// </summary>
    public static IServiceCollection AddPneumaticClient(this IServiceCollection services, string endpoint)
    {
        services.AddScoped(provider => PneumaticClient.ForHttp(provider.GetRequiredService<HttpClient>(), endpoint));
        return services;
    }
}
