using Microsoft.Data.Sqlite;
using Sample.Model;

/// <summary>
/// An in-process Scry server wired up exactly like <c>Sample.Server</c>'s <c>Program.cs</c>, but
/// hosted on <see cref="TestServer"/> so tests can drive the real query pipeline without a socket.
/// </summary>
public sealed class ScryTestServer : IAsyncDisposable
{
    readonly WebApplication app;
    readonly string dbPath;

    ScryTestServer(WebApplication app, string dbPath)
    {
        this.app = app;
        this.dbPath = dbPath;
    }

    public static async Task<ScryTestServer> StartAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"scry_sample_{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<SampleContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddScry(options =>
        {
            options.UseModel<SampleContext>();
            options.AddPocoSource(_ => Holiday.Seed());
            options.MaxPageSize = 200;
        });

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            SampleContext.Initialize(scope.ServiceProvider.GetRequiredService<SampleContext>());
        }

        app.MapScry("/api/query");
        await app.StartAsync();

        return new(app, dbPath);
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

        // The SQLite connection pool may still hold the temp file; release it before deleting.
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file.
        }
    }
}
