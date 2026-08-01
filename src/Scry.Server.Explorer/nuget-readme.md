# Scry.Server.Explorer

An opt-in, GraphiQL-style query explorer for [Scry](https://github.com/Papyrine/Scry). It serves a self-contained Blazor WebAssembly UI that runs Roslyn in the browser: write strongly-typed C# LINQ against the allow-listed sources with real IntelliSense and diagnostics, see the serialized wire request, execute it, and inspect the results. Only validated requests reach the server.

Map it alongside the query endpoint (it is off unless mapped, and Development-only by default):

```csharp
app.MapScry("/api/query");
app.MapScryExplorer("/scry");
```

Then browse to `/scry`. Configure via the options overload — for example to expose it outside Development behind a custom authorization check:

```csharp
app.MapScryExplorer(options =>
{
    options.Route = "/scry";
    options.QueryEndpoint = "/api/query";
    options.EnableGuard = _ => _.User.IsInRole("admin");
});
```

<!-- snippet: explorerOptions -->
<a id='snippet-explorerOptions'></a>
```cs
/// <summary>Sub-path the explorer UI is served under. Default <c>/scry</c>.</summary>
public string Route { get; set; } = "/scry";

/// <summary>The existing <c>MapScry</c> query endpoint the explorer POSTs validated requests to.
/// Default <c>/api/query</c>.</summary>
public string QueryEndpoint { get; set; } = "/api/query";

/// <summary>
/// Decides, per request, whether the explorer is reachable. Defaults to Development-only:
/// the explorer reveals the full queryable schema, so it stays off in production unless a host
/// opts in explicitly (e.g. behind an admin authorization check).
/// </summary>
public Func<HttpContext, bool> EnableGuard { get; set; } = DevelopmentOnly;

/// <summary>
/// Decides, per request, whether the explorer will show the SQL a query would run. Also
/// Development-only by default, and deliberately separate from <see cref="EnableGuard"/>: SQL
/// reveals more than the schema does — real table and column names, and the shape of any row
/// policy that narrowed the query — so opening the explorer to someone does not open this too.
/// </summary>
public Func<HttpContext, bool> EnableSqlPreview { get; set; } = DevelopmentOnly;
```
<sup><a href='/src/Scry.Server.Explorer/ScryExplorerOptions.cs#L10-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-explorerOptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Docs: [Query explorer](https://github.com/Papyrine/Scry/blob/main/docs/explorer.md)
