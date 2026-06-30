var builder = WebApplication.CreateBuilder(args);

// Serve the Blazor client's static web assets even when not running in the Development environment.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddDbContext<SampleContext>(_ => _.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ScrySample;Trusted_Connection=True;Encrypt=False"));

builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.AddPocoSource(_ => Holiday.Seed());
        _.MaxPageSize = 200;
    });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    SampleContext.Initialize(scope.ServiceProvider.GetRequiredService<SampleContext>());
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapScry("/api/query");
app.MapScryExplorer(
    _ =>
    {
        _.Route = "/scry";
        // This sample always exposes the explorer. The default guard is Development-only — in a real
        // app, run in Development or set EnableGuard to your own check (e.g. an admin authorization).
        _.EnableGuard = _ => true;
    });
app.MapFallbackToFile("index.html");

app.Run();
