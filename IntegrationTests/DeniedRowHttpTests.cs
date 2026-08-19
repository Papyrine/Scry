// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// What a policy configured to fail rather than hide looks like from the far side of HTTP: the status,
/// the body, the caching headers, and the exception the client raises for it.
/// </summary>
/// <remarks>
/// A server of its own, because the mode is a property of a registered policy and every other fixture
/// here depends on queries succeeding.
/// </remarks>
[TestFixture]
public class DeniedRowHttpTests
{
    static readonly SqlInstance<Sample.Model.SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            Sample.Model.SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    WebApplication app = null!;
    HttpClient http = null!;
    ScryQuery query = null!;
    SqlDatabase<Sample.Model.SampleContext> database = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<Sample.Model.SampleContext>(options =>
        {
            options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
            options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
            options.AddPolicy<Sample.Model.Order, NorthOrdersOnlyPolicy>(new()
            {
                RootList = DeniedRowMode.Error
            });
        });

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        query = new(ScryClient.ForHttp(http, "/api/query"));
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        await database.DisposeAsync();
    }

    [Test]
    public void ADeniedQuerySurfacesAsAPermissionException()
    {
        // Orders outside the north exist, so listing them all reads rows this policy denies.
        var exception = Assert.ThrowsAsync<ScryPermissionException>(
            () => query.Order.Select(_ => new {_.Region}).ToListAsync())!;

        Assert.That(exception.Message, Is.EqualTo(ScryPermissionException.DeniedMessage));
    }

    [Test]
    public async Task TheStatusIsForbiddenAndTheAnswerIsNotCacheable()
    {
        var request = QueryRequest.Create("Order", [new CountOp()]);
        using var content = new StringContent(ScryJson.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(response.Headers.ETag, Is.Null);
        Assert.That(response.Headers.CacheControl!.NoStore, Is.True);

        // The body says a policy denied the query and nothing else: not which source, not which row,
        // not which policy.
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain(ScryPermissionException.DeniedMessage));
        Assert.That(body, Does.Not.Contain("Order"));
        Assert.That(body, Does.Not.Contain("Region"));
    }

    [Test]
    public async Task AQueryThatReadsNoDeniedRowIsAnsweredNormally()
    {
        var rows = await query.Order
            .Where(_ => _.Region == "North")
            .Select(_ => new {_.Region, _.Amount})
            .ToListAsync();

        Assert.That(rows, Is.Not.Empty);
        Assert.That(rows.Select(_ => _.Region), Is.All.EqualTo("North"));
    }

    [Test]
    public void AStreamIsDeniedBeforeItStarts()
    {
        // The rows are built before the first byte is written, so a denial still answers as a status
        // rather than as an error marker part-way through a response that already looked successful.
        Assert.ThrowsAsync<ScryPermissionException>(
            async () =>
            {
                await foreach (var _ in query.Order.Select(order => new {order.Region}).ToAsyncEnumerable())
                {
                }
            });
    }
}

/// <summary>Scopes orders to one region, so the seeded rows outside it are ones a query loses.</summary>
public sealed class NorthOrdersOnlyPolicy :
    IReturnablePolicy<Sample.Model.Order>
{
    public IQueryable<Sample.Model.Order> Filter(IQueryable<Sample.Model.Order> source, ScryPolicyContext context) =>
        source.Where(_ => _.Region == "North");
}
