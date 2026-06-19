# Scry.Server.Explorer

An opt-in, GraphiQL-style query explorer for [Scry](https://github.com/Papyrine/Scry). It serves a
self-contained Blazor WebAssembly UI that runs Roslyn in the browser: write strongly-typed C# LINQ
against the allow-listed sources with real IntelliSense and diagnostics, see the serialized wire
request, execute it, and inspect the results. Only validated requests reach the server.

Map it alongside your query endpoint (it is off unless mapped, and Development-only by default):

```csharp
app.MapScry("/api/query");
app.MapScryExplorer("/scry");
```

Then browse to `/scry`. Configure via the options overload — for example to expose it outside
Development behind your own authorization check:

```csharp
app.MapScryExplorer(options =>
{
    options.Route = "/scry";
    options.QueryEndpoint = "/api/query";
    options.EnableGuard = context => context.User.IsInRole("admin");
});
```
