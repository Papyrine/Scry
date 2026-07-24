var builder = WebApplication.CreateBuilder(args);

// Serve the Blazor client's static web assets even when not running in the Development environment.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddDbContext<SampleContext>(_ => _.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ScrySample;Trusted_Connection=True;Encrypt=False"));

// begin-snippet: serverRegistration
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        // Holiday is a [QueryablePoco]: it has no table, so the server supplies its rows. Every
        // [QueryablePoco] type must be registered here or AddScry throws at startup.
        _.AddPocoSource(_ => Holiday.Seed());
        _.MaxPageSize = 200;
    });
// end-snippet

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    SampleContext.Initialize(scope.ServiceProvider.GetRequiredService<SampleContext>());
}

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

app.Run();
