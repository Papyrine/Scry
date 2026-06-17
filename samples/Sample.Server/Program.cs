using Microsoft.EntityFrameworkCore;
using Pneumatic;
using Sample.Model;

var builder = WebApplication.CreateBuilder(args);

// Serve the Blazor client's static web assets even when not running in the Development environment.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddDbContext<SampleContext>(options => options.UseSqlite("Data Source=sample.db"));

builder.Services.AddPneumatic(options =>
{
    options.UseModel<SampleContext>();
    options.AddPocoSource<Holiday>(_ => Holiday.Seed());
    options.MaxPageSize = 200;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    SampleContext.Initialize(scope.ServiceProvider.GetRequiredService<SampleContext>());
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapPneumatic("/api/query");
app.MapFallbackToFile("index.html");

app.Run();
