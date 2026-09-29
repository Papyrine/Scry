// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// The budget a server publishes for queries asked as a URL: how a client learns it, and what a
/// deployment that wants no URL form at all looks like from the outside.
/// </summary>
/// <remarks>
/// No database is reached here. Every request either fails validation — which happens before the
/// context is touched — or never gets past routing, so both servers are built over a connection string
/// nothing connects to. That is the point of the tests: none of this behaviour is about data.
/// </remarks>
[NotInParallel]
[DependsOn<HttpRoundTripTests.StaleClient>(nameof(HttpRoundTripTests.StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
public class UrlLimitTests
{
    const string unusable = "Server=(localdb)\\nothing;Database=none;Connect Timeout=1";

    static WebApplication Server(int limit)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(unusable));
        builder.Services.AddScry<Sample.Model.SampleContext>(
            options =>
            {
                options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
                options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
                options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
                options.QueryUrlLimit = limit;
            });

        var app = builder.Build();
        app.MapScry("/api/query");
        return app;
    }

    // The number is on every response, including a rejection — which is what lets a client learn it
    // from whatever it happened to ask first, rather than having to be told out of band.
    [Test]
    public async Task ClientAdoptsTheAdvertisedLimit()
    {
        await using var app = Server(64);
        await app.StartAsync();
        using var http = app.GetTestClient();

        var client = ScryClient.ForHttp(http, "/api/query");
        await Assert.That(client.QueryUrlLimit).IsEqualTo(QueryUrl.MaxLength);

        // Rejected by the allow-list, so nothing reaches the database — and the response still carries
        // the budget, because it is written before the request is even read.
        await Assert.ThrowsExactlyAsync<ScryRequestException>(
            () => client.Source<EmployeeQueryModel>("NotASource").CountAsync());

        await Assert.That(client.QueryUrlLimit).IsEqualTo(64);

        await app.StopAsync();
    }

    [Test]
    public async Task LimitIsAdvertisedOnEveryResponse()
    {
        await using var app = Server(2048);
        await app.StartAsync();
        using var http = app.GetTestClient();

        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}=not-base64url!!");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.Headers.GetValues(WireFormat.UrlLimitHeader).Single()).IsEqualTo("2048");

        await app.StopAsync();
    }

    // Zero is the one part of this setting that is enforced, and it is enforced by absence: the GET
    // route is never mapped, so routing answers it and no query ever reaches — or is logged by — Scry.
    [Test]
    public async Task ZeroMapsNoUrlRoute()
    {
        await using var app = Server(0);
        await app.StartAsync();
        using var http = app.GetTestClient();

        var encoded = QueryUrl.Encode(
            QueryRequest.Create("Employee", [new CountOp()]));
        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
            await Assert.That(response.Content.Headers.Allow).Contains("POST");
        }

        await app.StopAsync();
    }

    // A client that has heard zero stops offering URLs of its own accord, so the 405 above is the
    // backstop for a stale one rather than the everyday path.
    [Test]
    public async Task ZeroMakesTheClientAskWithABody()
    {
        await using var app = Server(0);
        await app.StartAsync();
        using var http = app.GetTestClient();

        var client = ScryClient.ForHttp(http, "/api/query");
        await Assert.ThrowsExactlyAsync<ScryRequestException>(
            () => client.Source<EmployeeQueryModel>("NotASource").CountAsync());

        await Assert.That(client.QueryUrlLimit).IsZero();

        await app.StopAsync();
    }

    [Test]
    public async Task NegativeLimitIsRefusedAtStartup()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Server(-1));

        await Assert.That(exception.Message).Contains(nameof(ScryOptions.QueryUrlLimit));
    }

    // A policied source answers differently for different callers, and an ETag over a URL says nothing
    // about which one asked. Caught where it can still be fixed rather than in production, where it
    // presents as one caller being handed another's rows.
    [Test]
    public async Task CachingAPoliciedSourceWithoutAScopeIsRefusedAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(unusable));
        builder.Services.AddScry<Sample.Model.SampleContext>(
            options =>
            {
                options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
                options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
                options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
                options.QueryFreshness = (_, _) => new("now");
            });

        var app = builder.Build();
        var exception = Assert.ThrowsExactly<Exception>(() => app.MapScry("/api/query"));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("Department");
            await Assert.That(exception.Message).Contains(nameof(ScryOptions.CacheScope));
        }
    }

    [Test]
    public async Task CachingAPoliciedSourceWithAScopeStarts()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(unusable));
        builder.Services.AddScry<Sample.Model.SampleContext>(
            options =>
            {
                options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
                options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
                options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
                options.QueryFreshness = (_, _) => new("now");
                options.CacheScope = _ => "tenant";
            });

        var app = builder.Build();

        await Assert.That(() => app.MapScry("/api/query")).ThrowsNothing();
    }
}
