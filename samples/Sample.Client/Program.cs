class Program
{
    static Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");

        // Blazor WebAssembly backs HttpClient with the browser's fetch API — there is no socket pool or DNS
        // lifetime to manage — so a plain scoped client is the right registration here; IHttpClientFactory
        // would add nothing. Reach for AddHttpClient only when you need a handler pipeline (an auth
        // DelegatingHandler, Polly, or multiple named clients).
        // begin-snippet: clientRegistration
        builder.Services.AddScoped(
            _ => new HttpClient
            {
                BaseAddress = new(builder.HostEnvironment.BaseAddress)
            });
        builder.Services.AddScryClient("/api/query");
        builder.Services.AddScoped<ScryQuery>();
        // end-snippet

        return builder.Build().RunAsync();
    }
}
