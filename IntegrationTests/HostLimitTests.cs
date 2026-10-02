// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.

using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// The limit the endpoints lean on the host for: the size of a request body. Scry refuses nothing by
/// size itself and never sizes a read to the declared length — the host bounds the body before a
/// handler reads it, per endpoint where the builder <c>MapScry</c> returns carries a request size
/// limit and by its own default otherwise. Hosted on Kestrel rather than the test server, which has
/// no body-size feature: the answer being pinned is the host's, given before Scry sees a byte.
/// </summary>
/// <remarks>
/// No database is reached. A refused body never reaches a handler, and the one request answered
/// reads a POCO source, so the server is built over a connection string nothing connects to.
/// </remarks>
[NotInParallel]
[DependsOn<HttpRoundTripTests.StaleClient>(nameof(HttpRoundTripTests.StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
public class HostLimitTests
{
    const string unusable = "Server=(localdb)\\nothing;Database=none;Connect Timeout=1";

    static WebApplication app = null!;
    static HttpClient http = null!;

    [Before(Class)]
    public static async Task StartServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var services = builder.Services;
        services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(unusable));
        services.AddScry<Sample.Model.SampleContext>(
            options =>
            {
                options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
                options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
                options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();

                // Mapped only where live queries are on, and it reads a body like the rest.
                options.MaxSubscriptions = 1;
            });

        app = builder.Build();
        // On what MapScry returns, so every endpoint it mapped is held to the one limit.
        app.MapScry("/api/query").WithMetadata(new RequestSizeLimitAttribute(1024));
        await app.StartAsync();

        http = new()
        {
            BaseAddress = new(app.Urls.Single())
        };
    }

    [After(Class)]
    public static async Task StopServer()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    // A request the server would otherwise answer, padded past the limit with whitespace the reader
    // ignores, so the refusal can only be the size — and it is the host's 413, never Scry's 500.
    [Test]
    [Arguments("/api/query")]
    [Arguments("/api/query/stream")]
    [Arguments("/api/query/batch")]
    [Arguments("/api/query/attachment")]
    [Arguments("/api/query/subscribe")]
    public async Task ABodyPastTheHostLimitIsRefusedByTheHost(string endpoint)
    {
        var body = """{"version":1,"root":"Holiday","pipeline":[{"$type":"count"}]}""" + new string(' ', 4096);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(endpoint, content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task ABodyWithinTheHostLimitIsAnswered()
    {
        var body = """{"version":1,"root":"Holiday","pipeline":[{"$type":"count"}]}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
