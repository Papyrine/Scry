var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// begin-snippet: clientRegistration
builder.Services.AddScoped(
    _ => new HttpClient
    {
        BaseAddress = new(builder.HostEnvironment.BaseAddress)
    });
builder.Services.AddScryClient("/api/query");
builder.Services.AddScoped<ScryQuery>();
// end-snippet

await builder.Build().RunAsync();
