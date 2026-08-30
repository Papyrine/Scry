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
[TestFixture]
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
        Assert.That(client.QueryUrlLimit, Is.EqualTo(QueryUrl.MaxLength));

        // Rejected by the allow-list, so nothing reaches the database — and the response still carries
        // the budget, because it is written before the request is even read.
        Assert.ThrowsAsync<ScryRequestException>(
            () => client.Source<EmployeeQueryModel>("NotASource").CountAsync());

        Assert.That(client.QueryUrlLimit, Is.EqualTo(64));

        await app.StopAsync();
    }

    [Test]
    public async Task LimitIsAdvertisedOnEveryResponse()
    {
        await using var app = Server(2048);
        await app.StartAsync();
        using var http = app.GetTestClient();

        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}=not-base64url!!");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Headers.GetValues(WireFormat.UrlLimitHeader).Single(), Is.EqualTo("2048"));

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

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
            Assert.That(response.Content.Headers.Allow, Does.Contain("POST"));
        });

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
        Assert.ThrowsAsync<ScryRequestException>(
            () => client.Source<EmployeeQueryModel>("NotASource").CountAsync());

        Assert.That(client.QueryUrlLimit, Is.Zero);

        await app.StopAsync();
    }

    [Test]
    public void NegativeLimitIsRefusedAtStartup()
    {
        var exception = Assert.Throws<Exception>(() => Server(-1));

        Assert.That(exception!.Message, Does.Contain(nameof(ScryOptions.QueryUrlLimit)));
    }

    // A policied source answers differently for different callers, and an ETag over a URL says nothing
    // about which one asked. Caught where it can still be fixed rather than in production, where it
    // presents as one caller being handed another's rows.
    [Test]
    public void CachingAPoliciedSourceWithoutAScopeIsRefusedAtStartup()
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
        var exception = Assert.Throws<Exception>(() => app.MapScry("/api/query"));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Department"));
            Assert.That(exception.Message, Does.Contain(nameof(ScryOptions.CacheScope)));
        });
    }

    [Test]
    public void CachingAPoliciedSourceWithAScopeStarts()
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

        Assert.DoesNotThrow(() => app.MapScry("/api/query"));
    }
}
