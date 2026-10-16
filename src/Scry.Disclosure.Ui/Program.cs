using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Scry;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");

        // Base address is the route the explorer is served under (e.g. /scry-disclosures/), so
        // "api/rows" resolves to the question the server mapped there.
        builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new(builder.HostEnvironment.BaseAddress) });
        builder.Services.AddScoped<DisclosureClient>();
        builder.Services.AddScoped<Session>();

        await builder.Build().RunAsync();
    }
}
