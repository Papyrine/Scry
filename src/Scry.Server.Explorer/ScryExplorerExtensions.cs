using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Scry;

/// <summary>Opt-in mapping for the Scry query explorer (a self-contained Blazor WASM debugging UI).</summary>
public static class ScryExplorerExtensions
{
    /// <summary>Maps the Scry query explorer under <paramref name="route"/> (default <c>/scry</c>).</summary>
    public static IEndpointConventionBuilder MapScryExplorer(
        this IEndpointRouteBuilder endpoints,
        string route = "/scry") =>
        endpoints.MapScryExplorer(_ => _.Route = route);

    /// <summary>Maps the Scry query explorer with explicit <see cref="ScryExplorerOptions"/>.</summary>
    public static IEndpointConventionBuilder MapScryExplorer(
        this IEndpointRouteBuilder endpoints,
        Action<ScryExplorerOptions> configure)
    {
        var options = new ScryExplorerOptions();
        configure(options);

        var basePath = "/" + options.Route.Trim('/');
        var assets = ExplorerAssets.Instance;

        var group = endpoints.MapGroup(basePath);
        // Schema introspection the UI reads on load (literal route wins over the asset catch-all).
        group.MapGet("/introspect", (HttpContext context, ScryProcessor processor) =>
            Introspect(context, options, processor));
        // The cast forces the RouteHandler (Delegate) overload; a bare HttpContext=>IResult lambda
        // would otherwise bind to the RequestDelegate overload and fail to compile.
        group.MapGet("", (Func<HttpContext, IResult>)(_ => Serve(_, path: null, options, basePath, assets)));
        group.MapGet("/{**path}", (HttpContext context, string path) =>
            Serve(context, path, options, basePath, assets));
        return group;
    }

    static IResult Serve(
        HttpContext context,
        string? path,
        ScryExplorerOptions options,
        string basePath,
        ExplorerAssets assets)
    {
        if (!options.EnableGuard(context))
        {
            // 404 (not 403) so a disabled explorer is indistinguishable from one that was never mapped.
            return Results.NotFound();
        }

        path = (path ?? "").Replace('\\', '/').Trim('/');

        // A path without a file extension is a client-side route (or the root) — serve the SPA host.
        if (path.Length == 0 || Path.GetExtension(path).Length == 0)
        {
            return Index(basePath, assets);
        }

        if (assets.TryOpen(path, out var stream, out var contentType))
        {
            return Results.Stream(stream, contentType);
        }

        return Results.NotFound();
    }

    static IResult Introspect(HttpContext context, ScryExplorerOptions options, ScryProcessor processor)
    {
        if (!options.EnableGuard(context))
        {
            return Results.NotFound();
        }

        var introspection = processor.Describe() with { QueryEndpoint = options.QueryEndpoint };
        return Results.Content(ScryJson.Serialize(introspection), "application/json");
    }

    static IResult Index(string basePath, ExplorerAssets assets)
    {
        var html = assets.ReadText("index.html")
            .Replace("__SCRY_BASE__", basePath + "/");

        return Results.Content(html, "text/html");
    }
}
