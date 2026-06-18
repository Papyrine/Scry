var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(
    _ => new HttpClient
    {
        BaseAddress = new(builder.HostEnvironment.BaseAddress)
    });
builder.Services.AddScryClient("/api/query");
builder.Services.AddScoped<ScryQuery>();

await builder.Build().RunAsync();
