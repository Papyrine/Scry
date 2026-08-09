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
                // Department.Handbook is an [Attachment], and one exposed without a check is a startup
                // failure. Registered here rather than by [AttachmentWith] because the model project
                // references the annotations alone and has no server type to name.
                _.AddAttachmentPolicy<Department, HandbookPolicy>();
                _.MaxPageSize = 200;
            });
        // end-snippet

        // Scry's telemetry is dormant until something subscribes; opting in is one AddSource and one
        // AddMeter. See /docs/observability.md for the spans, instruments, and tags.
        // begin-snippet: openTelemetry
        builder.Services.AddOpenTelemetry()
            .WithTracing(_ => _.AddSource(ScryInstrumentation.ActivitySourceName))
            .WithMetrics(_ => _.AddMeter(ScryInstrumentation.MeterName));
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
