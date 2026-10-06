namespace Scry;

/// <summary>
/// Opt-in mapping for the disclosure explorer: a self-contained browser UI over the record the
/// disclosure audit keeps, for whoever has to answer who saw what.
/// </summary>
public static class ScryDisclosureExplorerExtensions
{
    /// <summary>Maps the disclosure explorer under <paramref name="route"/> (default <c>/scry-disclosures</c>).</summary>
    public static IEndpointConventionBuilder MapScryDisclosureExplorer(
        this IEndpointRouteBuilder endpoints,
        string route = "/scry-disclosures") =>
        endpoints.MapScryDisclosureExplorer(_ => _.Route = route);

    /// <summary>Maps the disclosure explorer with explicit <see cref="ScryDisclosureExplorerOptions"/>.</summary>
    /// <remarks>
    /// <para>
    /// Everything it maps is behind <see cref="ScryDisclosureExplorerOptions.EnableGuard"/> and
    /// answers <c>404</c> where that says no, so a closed explorer cannot be told from one never
    /// mapped. <c>RequireAuthorization</c> on what this returns reaches every route.
    /// </para>
    /// <para>
    /// Reading the record is recorded. Every question is written to the record, through the same
    /// sink the answers went through, before it is answered — and is not answered where that fails.
    /// </para>
    /// </remarks>
    public static IEndpointConventionBuilder MapScryDisclosureExplorer(
        this IEndpointRouteBuilder endpoints,
        Action<ScryDisclosureExplorerOptions> configure)
    {
        var options = new ScryDisclosureExplorerOptions();
        configure(options);
        if (options.ExportLimit < 1)
        {
            throw new ArgumentException($"{nameof(ScryDisclosureExplorerOptions)}.{nameof(options.ExportLimit)} must be greater than zero. {nameof(options.EnableExport)} is what turns exporting off.");
        }

        var basePath = "/" + options.Route.Trim('/');
        var assets = ExplorerAssets.Instance;
        if (!assets.HasAssets)
        {
            // At startup rather than as a 500 on the first visit, as the query explorer does.
            throw new InvalidOperationException(
                "Scry.Server.Disclosure.Explorer holds no embedded UI, so MapScryDisclosureExplorer has nothing to serve. " +
                "The UI is published and embedded by the EmbedDisclosureUi target in Scry.Server.Disclosure.Explorer.csproj; " +
                "a package or local build without it is incomplete.");
        }

        Require(endpoints.ServiceProvider);

        // Built here rather than per request: everything about the page is fixed by the route.
        var page = Build(assets.ReadText("index.html"), basePath);

        var group = endpoints.MapGroup(basePath);

        // Before the catch-all below, though a literal route would win over it anyway.
        new DisclosureApi(options).Map(group.MapGroup("/api"));

        // The cast forces the RouteHandler (Delegate) overload, as in MapScryExplorer.
        group.MapGet("", (Func<HttpContext, IResult>) (_ => Serve(_, path: null, options, assets, page)));
        group.MapGet("/{**path}", (HttpContext context, string path) =>
            Serve(context, path, options, assets, page));
        return group;
    }

    // Said at startup, where whoever wired the host is reading, rather than as a failure the first
    // time somebody opens the page.
    static void Require(IServiceProvider services)
    {
        if (services.GetService<ScryOptions>()?.Disclosure is null)
        {
            throw new InvalidOperationException(
                "MapScryDisclosureExplorer shows the record the disclosure audit keeps, and this host keeps none. " +
                "Turn the audit on in AddScry, with UseDisclosureAudit or UseSqlServerDisclosureAudit, before mapping the explorer.");
        }

        if (services.GetService<IScryDisclosureReader>() is null)
        {
            throw new InvalidOperationException(
                "MapScryDisclosureExplorer needs something to read the record with, and no IScryDisclosureReader is registered. " +
                "UseSqlServerDisclosureAudit registers its store as one, and a ScryMemoryDisclosureStore handed to UseDisclosureAudit is registered as one too. " +
                "For any other store, register what reads it as IScryDisclosureReader.");
        }
    }

    /// <summary>
    /// The policy the host page is served under. Narrower than the query explorer's, which has an
    /// editor to run: every source is this origin, there is no inline style and no worker, the page
    /// may be framed by nobody, and it may call nothing but the origin it came from — so what it is
    /// shown has nowhere else to go.
    /// </summary>
    static string ContentSecurityPolicy(IReadOnlyList<string> hashes)
    {
        string[] scripts = ["'self'", "'wasm-unsafe-eval'", .. hashes];
        return string.Join(
            "; ",
            "default-src 'self'",
            $"script-src {string.Join(' ', scripts)}",
            "style-src 'self'",
            "img-src 'self' data:",
            "font-src 'self'",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "frame-ancestors 'none'",
            "form-action 'none'");
    }

    static IResult Serve(
        HttpContext context,
        string? path,
        ScryDisclosureExplorerOptions options,
        ExplorerAssets assets,
        DisclosurePage page)
    {
        if (!options.EnableGuard(context))
        {
            // 404 (not 403) so a closed explorer is indistinguishable from one that was never mapped.
            return Results.NotFound();
        }

        // Nothing here is to be read as anything but what it says it is, and nothing it links to is
        // told where the reader came from.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        path = (path ?? "").Replace('\\', '/').Trim('/');

        // A path without a file extension is the page itself.
        if (path.Length == 0 || Path.GetExtension(path).Length == 0)
        {
            // Asked for without its trailing slash, it is sent to the address with one. The page's
            // base is the route with the slash, and what it is asked lives in the fragment: a page
            // sitting at the address without one is not inside its own base, which the page's router
            // will not stand for. A browser carries the fragment across the redirect by itself.
            if (path.Length == 0 &&
                context.Request.Path.Value is { } asked &&
                !asked.EndsWith('/'))
            {
                return Results.Redirect($"{context.Request.PathBase}{asked}/{context.Request.QueryString}");
            }

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

    // The page and its assets are the program, not the record: they hold nobody's data, so they are
    // revalidated rather than refused to every cache, for the reason the query explorer gives. What
    // the page is told — the answers — is never stored anywhere; see DisclosureApi.
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

    static IResult Index(HttpContext context, DisclosurePage page)
    {
        // On the 304 as well: a browser folds a 304's headers into the copy it kept.
        context.Response.Headers.ContentSecurityPolicy = page.Policy;

        if (Unchanged(context, page.Tag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Content(page.Html, "text/html");
    }

    /// <summary>
    /// The page, its policy and its tag from the embedded page's text: the page with its base href
    /// written in, the policy naming the hashes of the inline scripts that text holds, and the tag of
    /// those same bytes. Reachable so the three can be asserted against a page of a test's own.
    /// </summary>
    internal static DisclosurePage Build(string html, string basePath)
    {
        html = html.Replace("__SCRY_BASE__", basePath.TrimEnd('/') + "/");
        return new(
            html,
            // Hashed after the rewrite, so a token that ever moves inside a script takes its hash with it.
            ContentSecurityPolicy(ExplorerAssets.InlineScriptHashes(html)),
            $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html)))}\"");
    }
}

/// <summary>The host page, its Content-Security-Policy and its entity tag, as one mapping serves them.</summary>
sealed record DisclosurePage(string Html, string Policy, string Tag);
