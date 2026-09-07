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
        if (!assets.HasAssets)
        {
            // At startup rather than as a 500 on the first visit. A build that embedded no UI can
            // never serve the explorer, whatever the guard decides, so the only thing waiting
            // achieves is that the sentence arrives as a stack trace to whoever browsed there.
            throw new InvalidOperationException(
                "Scry.Server.Explorer holds no embedded explorer UI, so MapScryExplorer has nothing to serve. " +
                "The UI is published and embedded by the EmbedExplorerUi target in Scry.Server.Explorer.csproj; " +
                "a package or local build without it is incomplete.");
        }

        // Built here rather than per request: everything about the page is fixed by the route.
        var page = Render(options, basePath, assets);

        var group = endpoints.MapGroup(basePath);
        // Schema introspection the UI reads on load (literal route wins over the asset catch-all).
        group.MapGet("/introspect", (HttpContext context, ScryProcessor processor) =>
            Introspect(context, options, processor));
        group.MapPost("/sql", (HttpContext context, ScryProcessor processor) =>
            Sql(context, options, processor));
        // The cast forces the RouteHandler (Delegate) overload; a bare HttpContext=>IResult lambda
        // would otherwise bind to the RequestDelegate overload and fail to compile.
        group.MapGet("", (Func<HttpContext, IResult>) (_ => Serve(_, path: null, options, assets, page)));
        group.MapGet("/{**path}", (HttpContext context, string path) =>
            Serve(context, path, options, assets, page));
        return group;
    }

    /// <summary>
    /// The policy the host page is served under. Every source is the explorer's own origin, and the
    /// allowances past that are the ones the page cannot run without: <c>'wasm-unsafe-eval'</c> for
    /// the .NET runtime, inline styles for Monaco (which writes its theme into style elements it
    /// creates), and <c>blob:</c> workers for Monaco too (its worker factory wraps the same-origin
    /// worker script in a blob, and a blob worker inherits this policy, so the script it imports is
    /// still held to <c>'self'</c>). The page's own inline scripts are allowed by hash rather than
    /// by nonce — see <see cref="ExplorerAssets.InlineScriptHashes"/>.
    /// </summary>
    /// <remarks>
    /// Only the document carries it: a policy governs what a page loads and runs, and the assets the
    /// page loads are governed by the page's. <c>connect-src</c> is the origin alone unless
    /// <see cref="ScryExplorerOptions.QueryEndpoint"/> names another, which is then the one other
    /// origin the page may call.
    /// </remarks>
    static string ContentSecurityPolicy(ScryExplorerOptions options, IReadOnlyList<string> hashes)
    {
        var connect = "'self'";
        if (Uri.TryCreate(options.QueryEndpoint, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme is "http" or "https")
        {
            // Scheme and authority, not GetLeftPart(UriPartial.Authority), which keeps any userinfo the
            // endpoint carried. A host-source has no room for one, so the browser would drop the whole
            // expression as unparseable and refuse every call the explorer makes to that origin.
            connect += $" {endpoint.Scheme}://{endpoint.Authority}";
        }

        string[] scripts = ["'self'", "'wasm-unsafe-eval'", .. hashes];
        return string.Join(
            "; ",
            "default-src 'self'",
            $"script-src {string.Join(' ', scripts)}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "font-src 'self' data:",
            $"connect-src {connect}",
            "worker-src 'self' blob:",
            "object-src 'none'",
            "base-uri 'self'",
            "frame-ancestors 'self'",
            "form-action 'none'");
    }

    static IResult Serve(
        HttpContext context,
        string? path,
        ScryExplorerOptions options,
        ExplorerAssets assets,
        ExplorerPage page)
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
            return Index(context, page);
        }

        if (assets.TryOpen(path, out var stream, out var contentType, out var tag))
        {
            if (Unchanged(context, tag))
            {
                stream.Dispose();
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Stream(stream, contentType);
        }

        return Results.NotFound();
    }

    /// <summary>
    /// Sets the response's cache validators and answers whether the caller already holds the bytes.
    /// </summary>
    /// <remarks>
    /// Every asset asks to be revalidated rather than cached blind or not at all. The
    /// <c>_framework</c> names are stable across releases — the executor fetches the client and wire
    /// assemblies by name — so a cache that kept an old assembly under a new boot manifest would fail
    /// the integrity check the manifest declares, and one that kept nothing would download Roslyn on
    /// every visit. A tag from the embedded content hash makes an unchanged asset a 304 and a changed
    /// one new bytes, at the cost of one conditional request each.
    /// </remarks>
    static bool Unchanged(HttpContext context, string? tag)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-cache";
        if (tag is null)
        {
            return false;
        }

        var ours = new EntityTagHeaderValue(tag);
        headers.ETag = ours.ToString();
        return EntityTagHeaderValue.TryParseList(context.Request.Headers.IfNoneMatch, out var held) &&
               held.Any(_ => _.Equals(EntityTagHeaderValue.Any) || _.Compare(ours, useStrongComparison: false));
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

        // The same rule the query endpoints apply: a form cannot send application/json, so requiring it
        // keeps a cross-site navigation from reaching this.
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ||
            !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(
                new ScryError("A request body must be sent as application/json."),
                ScryJson.Options,
                statusCode: StatusCodes.Status415UnsupportedMediaType);
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
        catch (Exception exception)
            when (exception is ScryValidationException or ScryWireException)
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
    // ReSharper disable once NotAccessedPositionalProperty.Local
    sealed record SqlPreview(string Sql);

    static IResult Index(HttpContext context, ExplorerPage page)
    {
        // On the 304 as well: a browser folds a 304's headers into the copy it kept, so the policy the
        // cached page runs under is this one rather than the one it was first served with.
        context.Response.Headers.ContentSecurityPolicy = page.Policy;

        if (Unchanged(context, page.Tag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Content(page.Html, "text/html");
    }

    /// <summary>
    /// The host page as it is served, built once per mapping. Everything about it is fixed by the
    /// route: the page is the embedded one with its base href written in, the policy names the hashes
    /// of the inline scripts <em>that</em> text holds, and the tag is of those same bytes.
    /// </summary>
    static ExplorerPage Render(ScryExplorerOptions options, string basePath, ExplorerAssets assets) =>
        Build(assets.ReadText("index.html"), basePath, options);

    /// <summary>
    /// The page, its policy and its tag from the embedded page's text. Separated from
    /// <see cref="Render"/> so the three can be asserted against a page of the test's own.
    /// </summary>
    internal static ExplorerPage Build(string html, string basePath, ScryExplorerOptions options)
    {
        html = html.Replace("__SCRY_BASE__", basePath.TrimEnd('/') + "/");

        return new(
            html,
            // Hashed after the rewrite, so a token that ever moves inside a script takes its hash with
            // it rather than invalidating it.
            ContentSecurityPolicy(options, ExplorerAssets.InlineScriptHashes(html)),
            // Tagged from what is served rather than from the embedded file: the route is written in.
            $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html)))}\"");
    }
}

/// <summary>The host page, its Content-Security-Policy and its entity tag, as one mapping serves them.</summary>
sealed record ExplorerPage(string Html, string Policy, string Tag);
