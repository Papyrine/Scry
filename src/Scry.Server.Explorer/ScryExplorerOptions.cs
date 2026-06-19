using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Scry;

/// <summary>Configures the opt-in Scry query explorer mapped by <see cref="ScryExplorerExtensions.MapScryExplorer(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, System.Action{ScryExplorerOptions})"/>.</summary>
public sealed class ScryExplorerOptions
{
    /// <summary>Sub-path the explorer UI is served under. Default <c>/scry</c>.</summary>
    public string Route { get; set; } = "/scry";

    /// <summary>The existing <c>MapScry</c> query endpoint the explorer POSTs validated requests to. Default <c>/api/query</c>.</summary>
    public string QueryEndpoint { get; set; } = "/api/query";

    /// <summary>
    /// Decides, per request, whether the explorer is reachable. Defaults to Development-only:
    /// the explorer reveals the full queryable schema, so it stays off in production unless a host
    /// opts in explicitly (e.g. behind an admin authorization check).
    /// </summary>
    public Func<HttpContext, bool> EnableGuard { get; set; } = DevelopmentOnly;

    /// <summary>The default <see cref="EnableGuard"/>: enabled only in the Development environment.</summary>
    public static bool DevelopmentOnly(HttpContext context) =>
        context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
}
