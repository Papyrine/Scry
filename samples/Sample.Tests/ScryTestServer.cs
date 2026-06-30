using Sample.Model;

/// <summary>
/// An in-process Scry server wired up exactly like <c>Sample.Server</c>'s <c>Program.cs</c>, but
/// hosted on <see cref="TestServer"/> so tests can drive the real query pipeline without a socket.
/// Each instance runs against its own LocalDB database, cloned from a seeded template.
/// </summary>
public sealed class ScryTestServer : IAsyncDisposable
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

    public static async Task<ScryTestServer> StartAsync()
    {
        var database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<SampleContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<SampleContext>(options =>
        {
            options.AddPocoSource(_ => Holiday.Seed());
            options.MaxPageSize = 200;
        });

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        return new(app, database);
    }

    /// <summary>An <see cref="HttpClient"/> bound to the test server, rooted at the query endpoint.</summary>
    public HttpClient CreateClient() => app.GetTestClient();

    /// <summary>The raw handler, for composing into a custom <see cref="HttpClient"/> pipeline.</summary>
    public HttpMessageHandler CreateHandler() => app.GetTestServer().CreateHandler();

    /// <summary>A <see cref="ScryClient"/> pointed at the live query endpoint.</summary>
    public ScryClient CreateScryClient() => ScryClient.ForHttp(CreateClient(), "/api/query");

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        await database.DisposeAsync();
    }
}
