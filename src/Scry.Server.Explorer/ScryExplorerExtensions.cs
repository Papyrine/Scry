using System.Text.Json;
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
        group.MapPost("/sql", (HttpContext context, ScryProcessor processor) =>
            Sql(context, options, processor));
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

        var introspection = processor.Describe() with
        {
            QueryEndpoint = options.QueryEndpoint,
            // Advertised so the UI can offer the SQL pane only where it would work, rather than
            // showing a control that 404s.
            SqlPreview = options.EnableSqlPreview(context)
        };
        return Results.Content(ScryJson.Serialize(introspection), "application/json");
    }

    /// <summary>
    /// Shows the SQL a request would run, without running it. Behind its own guard on top of the
    /// explorer's, because SQL reveals more than the schema: real table and column names, and the shape
    /// of any row policy. The request is validated and policy-filtered exactly as a query would be, so
    /// nothing is previewable that would not have been runnable.
    /// </summary>
    static async Task<IResult> Sql(HttpContext context, ScryExplorerOptions options, ScryProcessor processor)
    {
        if (!options.EnableGuard(context) ||
            !options.EnableSqlPreview(context))
        {
            return Results.NotFound();
        }

        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        try
        {
            var request = ScryJson.DeserializeRequest(body);
            var sql = processor.ToQueryString(request, context.RequestServices);
            return Results.Content(
                JsonSerializer.Serialize(new SqlPreview(sql), ScryJson.Options),
                "application/json");
        }
        catch (Exception exception) when (exception is ScryValidationException or ScryWireException)
        {
            return Results.Json(new ScryError(exception.Message), ScryJson.Options, statusCode: 400);
        }
        catch (Exception)
        {
            // Same rule the query endpoint follows: nothing internal leaves the server.
            return Results.Json(new ScryError("Reading the query's SQL failed."), ScryJson.Options, statusCode: 500);
        }
    }

    /// <summary>The SQL preview response body. Explorer-only — not part of the wire contract.</summary>
    sealed record SqlPreview(string Sql);

    static IResult Index(string basePath, ExplorerAssets assets)
    {
        var html = assets.ReadText("index.html")
            .Replace("__SCRY_BASE__", basePath + "/");

        return Results.Content(html, "text/html");
    }
}
