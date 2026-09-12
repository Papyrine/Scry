class Program
{
    static Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");

        // Naming the client keeps Scry's base address — and any handler pipeline it grows, an auth
        // DelegatingHandler or a retry policy — separate from every other call the app makes, so nothing
        // depends on which HttpClient the container happens to hold. WebAssembly is the one host where
        // the plain AddScryClient(endpoint) overload is equally correct: the browser backs HttpClient
        // there, so there is no socket pool or DNS lifetime for the factory to manage.
        // begin-snippet: clientRegistration
        builder.Services.AddHttpClient(
            "scry",
            _ => _.BaseAddress = new(builder.HostEnvironment.BaseAddress));
        builder.Services.AddScryClient(
            "/api/query",
            _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
        builder.Services.AddScoped<ScryQuery>();
        // end-snippet

        // The sample's own endpoints — the grant state behind the cached row policy — are not Scry's,
        // so they get a client of their own rather than the one below, whose whole job is to answer
        // repeats without asking. This page asks on purpose.
        builder.Services.AddHttpClient(
            "api",
            _ => _.BaseAddress = new(builder.HostEnvironment.BaseAddress));

        // The client half of the server's 304: re-ask with If-None-Match, and answer a 304 from what
        // was kept last time. One handler in the pipeline, invisible above it — see /docs/caching.md.
        // The store is the singleton because the handler is not: the factory rotates handlers, and a
        // cache that rotated with them would forget everything every couple of minutes.
        // begin-snippet: clientCacheRegistration
        builder.Services.AddSingleton<QueryCache>();
        builder.Services.AddTransient<QueryCacheHandler>();
        builder.Services
            .AddHttpClient("scry")
            .AddHttpMessageHandler<QueryCacheHandler>();
        // end-snippet

        // The debug sidecar: records every Scry exchange on the named client and shows them in a
        // panel toggled by Alt+Q. Registered after the cache handler so what it sees is the real
        // wire exchange — the If-None-Match request and the raw 304 — rather than the replay.
        // begin-snippet: sidecarRegistration
        builder.Services.AddScrySidecar();
        builder.Services
            .AddHttpClient("scry")
            .AddHttpMessageHandler<ScrySidecarHandler>();
        // end-snippet

        return builder.Build().RunAsync();
    }
}
