using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Sample.WpfClient;

public partial class App
{
    ServiceProvider provider = null!;
    IServiceScope scope = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // Naming the client keeps Scry's base address, and any handler pipeline it grows, separate from
        // every other call the app makes. The shorter AddScryClient(endpoint) overload takes whichever
        // HttpClient the container happens to hold, which suits Blazor WebAssembly and little else.
        // begin-snippet: desktopClientRegistration
        services.AddHttpClient(
            "scry",
            _ => _.BaseAddress = new(ServerAddress));
        services.AddScryClient(
            "/api/query",
            _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
        services.AddScoped<ScryQuery>();
        // end-snippet

        provider = services.BuildServiceProvider();

        // ScryClient is registered scoped: it records the schema stamp each response advertises and
        // raises SchemaStaleDetected at most once, so resolving a fresh one per window would reset
        // that and never report drift. A desktop app has no request to scope to, so it opens one
        // scope at startup and holds it for as long as the app runs.
        // begin-snippet: desktopClientScope
        scope = provider.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<ScryQuery>();
        // end-snippet

        new MainWindow(query).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        scope.Dispose();
        provider.Dispose();
        base.OnExit(e);
    }

    /// <summary>Where Sample.WebServer listens, per its launchSettings.json.</summary>
    internal const string ServerAddress = "http://localhost:5000";
}
