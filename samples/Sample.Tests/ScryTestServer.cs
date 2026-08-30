using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Sample.Model;

/// <summary>
/// An in-process Scry server wired up exactly like <c>Sample.Server</c>'s <c>Program.cs</c>, but
/// hosted on <see cref="TestServer"/> so tests can drive the real query pipeline without a socket.
/// Each instance runs against its own LocalDB database, cloned from a seeded template.
/// </summary>
public sealed class ScryTestServer :
    IAsyncDisposable
{
    static SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    WebApplication app;
    SqlDatabase<SampleContext> database;

    ScryTestServer(WebApplication app, SqlDatabase<SampleContext> database)
    {
        this.app = app;
        this.database = database;
    }

    /// <param name="conditionalRequests">
    /// Answers repeats conditionally, as <c>Program.cs</c> does. Off by default: it puts an
    /// <c>ETag</c> on every URL-borne response, and the value moves with the database's log position,
    /// which would be churn in the snapshots of the other fixtures that share one of these servers.
    /// </param>
    public static async Task<ScryTestServer> StartAsync(bool conditionalRequests = false)
    {
        // A database of its own when conditional requests are on: that fixture writes, and every other
        // in-process fixture shares one of these servers and asserts against the seeded rows. Without
        // the suffix they all resolve to the same database name, since it is derived from this method.
        var database = await sqlInstance.Build(databaseSuffix: conditionalRequests ? "etag" : null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<SampleContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddSingleton<RegionGrants>();
        builder.Services.AddSingleton<RegionAccessPolicy>();
        builder.Services.AddScry<SampleContext>(options =>
        {
            options.AddPocoSource(_ => Holiday.Seed());
            options.AddAttachmentPolicy<Department, HandbookPolicy>();
            options.AddAttachmentPolicy<Employee, PhotoPolicy>();
            options.MaxPageSize = 200;
            options.AddCachedPolicy<Order, long, RegionAccessPolicy>(_ => _.Revision);
            if (conditionalRequests)
            {
                options.UseDeltaFreshness<SampleContext>();
                options.CacheScope = _ => $"sample-{_.RequestServices.GetRequiredService<RegionGrants>().Version}";
            }
        });

        var app = builder.Build();
        app.MapScry("/api/query");

        // The sample's own endpoints behind the cached row policy, mirrored from Program.cs so the
        // page under test drives the same two things here as it does in the running app.
        app.MapGet("/api/grants", (RegionGrants grants) =>
            new GrantState([.. RegionGrants.Regions], [.. grants.For("sample")], grants.Lookups));

        app.MapPost("/api/grants/{region}", (string region, bool allowed, RegionGrants grants, ScryPolicyCache cache) =>
        {
            grants.Set("sample", region, allowed);
            cache.InvalidateScope<Order>("sample");
            return Results.NoContent();
        });

        app.MapPost("/api/orders/{id:int}/touch", async (int id, SampleContext data) =>
        {
            var order = await data.Orders.FindAsync(id);
            if (order is null)
            {
                return Results.NotFound();
            }

            order.Revision = await EntityFrameworkQueryableExtensions.MaxAsync(data.Orders, _ => _.Revision) + 1;
            await data.SaveChangesAsync();
            return Results.NoContent();
        });

        await app.StartAsync();

        return new(app, database);
    }

    /// <summary>A context over this server's database, for a test that needs to write to it.</summary>
    public SampleContext NewContext() =>
        database.NewDbContext();

    /// <summary>An <see cref="HttpClient"/> bound to the test server, rooted at the query endpoint.</summary>
    public HttpClient CreateClient() =>
        app.GetTestClient();

    /// <summary>The raw handler, for composing into a custom <see cref="HttpClient"/> pipeline.</summary>
    public HttpMessageHandler CreateHandler() =>
        app.GetTestServer().CreateHandler();

    /// <summary>A <see cref="ScryClient"/> pointed at the live query endpoint.</summary>
    public ScryClient CreateScryClient() =>
        ScryClient.ForHttp(CreateClient(), "/api/query");

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        await database.DisposeAsync();
    }
}
