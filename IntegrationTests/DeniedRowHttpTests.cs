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
[NotInParallel]
[DependsOn<HttpRoundTripTests.StaleClient>(nameof(HttpRoundTripTests.StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
public class DeniedRowHttpTests
{
    static SqlInstance<Sample.Model.SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            Sample.Model.SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    static WebApplication app = null!;
    static HttpClient http = null!;
    static ScryQuery query = null!;
    static SqlDatabase<Sample.Model.SampleContext> database = null!;

    [Before(Class)]
    public static async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<Sample.Model.SampleContext>(options =>
        {
            options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
            options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
            options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
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

    [After(Class)]
    public static async Task StopServer()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        await database.DisposeAsync();
    }

    [Test]
    public async Task ADeniedQuerySurfacesAsAPermissionException()
    {
        // Orders outside the north exist, so listing them all reads rows this policy denies.
        var exception = (await Assert.ThrowsExactlyAsync<ScryPermissionException>(
            () => query.Order.Select(_ => new {_.Region}).ToListAsync()))!;

        await Assert.That(exception.Message).IsEqualTo(ScryPermissionException.DeniedMessage);
    }

    [Test]
    public async Task TheStatusIsForbiddenAndTheAnswerIsNotCacheable()
    {
        var request = QueryRequest.Create("Order", [new CountOp()]);
        using var content = new StringContent(ScryJson.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(response.Headers.ETag).IsNull();
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();

        // The body says a policy denied the query and nothing else: not which source, not which row,
        // not which policy.
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains(ScryPermissionException.DeniedMessage);
        await Assert.That(body).DoesNotContain("Order");
        await Assert.That(body).DoesNotContain("Region");
    }

    [Test]
    public async Task AQueryThatReadsNoDeniedRowIsAnsweredNormally()
    {
        var rows = await query.Order
            .Where(_ => _.Region == "North")
            .Select(_ => new {_.Region, _.Amount})
            .ToListAsync();

        await Assert.That(rows).IsNotEmpty();
        await Assert.That(rows.Select(_ => _.Region)).All(_ => Equals(_, "North"));
    }

    [Test]
    public async Task AStreamIsDeniedBeforeItStarts() =>
        // The rows are built before the first byte is written, so a denial still answers as a status
        // rather than as an error marker part-way through a response that already looked successful.
        await Assert.ThrowsExactlyAsync<ScryPermissionException>(
            async () =>
            {
                await foreach (var _ in query.Order.Select(_ => new {_.Region}).ToAsyncEnumerable())
                {
                }
            });
}

/// <summary>Scopes orders to one region, so the seeded rows outside it are ones a query loses.</summary>
public sealed class NorthOrdersOnlyPolicy :
    IReturnablePolicy<Sample.Model.Order>
{
    public IQueryable<Sample.Model.Order> Filter(IQueryable<Sample.Model.Order> source, ScryPolicyContext context) =>
        source.Where(_ => _.Region == "North");
}
