using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Sample.WinFormsClient;

static class Program
{
    /// <summary>Where Sample.WebServer listens, per its launchSettings.json.</summary>
    internal const string ServerAddress = "http://localhost:5000";

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // The same registration the WPF sample uses. Nothing about it is specific to either UI
        // framework: naming the client keeps Scry's base address, and any handler pipeline it grows,
        // separate from every other call the app makes.
        var services = new ServiceCollection();
        services.AddHttpClient(
            "scry",
            _ => _.BaseAddress = new(ServerAddress));
        services.AddScryClient(
            "/api/query",
            _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
        services.AddScoped<ScryQuery>();

        using var provider = services.BuildServiceProvider();

        // One scope for as long as the app runs, rather than one per form. ScryClient is registered
        // scoped because it records the schema stamp each response advertises and raises
        // SchemaStaleDetected at most once, so a fresh instance per form would never report drift.
        using var scope = provider.CreateScope();

        Application.Run(new MainForm(scope.ServiceProvider.GetRequiredService<ScryQuery>()));
    }
}
