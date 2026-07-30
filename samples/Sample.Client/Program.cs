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

        return builder.Build().RunAsync();
    }
}
