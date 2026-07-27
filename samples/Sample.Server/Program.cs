class Program
{
    // Back the sample with EfLocalDb: each launch runs against its own LocalDB database, cloned from a
    // seeded template. EfLocalDb manages its own instance and self-heals orphaned files, so the sample
    // can never wedge on a leftover .mdf the way a fixed-name EnsureCreated database can.
    static SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    static async Task Main(string[] args)
    {
        var database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder(args);

        // Serve the Blazor client's static web assets even when not running in the Development environment.
        builder.WebHost.UseStaticWebAssets();

        builder.Services
            .AddDbContext<SampleContext>(_ => _.UseSqlServer(database.ConnectionString));

        // begin-snippet: serverRegistration
        builder.Services
            .AddScry<SampleContext>(
            _ =>
            {
                // Holiday is a [QueryablePoco]: it has no table, so the server supplies its rows. Every
                // [QueryablePoco] type must be registered here or AddScry throws at startup.
                _.AddPocoSource(_ => Holiday.Seed());
                _.MaxPageSize = 200;
            });
        // end-snippet

        var app = builder.Build();

        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles();

        // begin-snippet: mapScry
        app.MapScry("/api/query");
        // end-snippet
        // begin-snippet: mapExplorer
        app.MapScryExplorer(
            _ =>
            {
                _.Route = "/scry";
                // This sample always exposes the explorer. The default guard is Development-only — in a real
                // app, run in Development or set EnableGuard to your own check (e.g. an admin authorization).
                _.EnableGuard = _ => true;
            });
        // end-snippet
        app.MapFallbackToFile("index.html");

        await app.RunAsync();
    }
}
