// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

[TestFixture]
public class HttpRoundTripTests
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
    ScryClient client = null!;
    ScryQuery query = null!;
    SqlDatabase<Sample.Model.SampleContext> database = null!;

    record EmployeeRow(string Name, Status Status, string? Manager, string Department);

    record RegionSummary(string Region, decimal Total, int Count);

    record HeadcountRow(string Department, int Headcount);

    static readonly string[] activeEmployeeNames = ["Aaron", "Alice", "Carol"];

    static readonly string[] departmentNames = ["Engineering", "Sales"];

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
            options.MaxPageSize = 200;
        });

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        client = ScryClient.ForHttp(http, "/api/query");
        query = new(client);
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
    public async Task EmployeesProjectionOverHttp()
    {
        var rows = await query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(activeEmployeeNames));
        Assert.That(rows[0].Manager, Is.EqualTo("Alice"));
        Assert.That(rows[1].Manager, Is.Null);
        Assert.That(rows[0].Department, Is.EqualTo("Engineering"));
    }

    [Test]
    public async Task ViewProjectionOverHttp()
    {
        // EmployeeSummary is a keyless [QueryableView] mapped to a SQL view. This confirms a view
        // round-trips the full pipeline: source discovery, validation, EF Set<T> against the view,
        // projection, and HTTP. The seed puts two employees in each of the two departments.
        var rows = await query.EmployeeSummary
            .OrderBy(_ => _.Department)
            .Select(_ => new HeadcountRow(_.Department, _.Headcount))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Department), Is.EqualTo(departmentNames));
        Assert.That(rows.Sum(_ => _.Headcount), Is.EqualTo(4));
    }

    [Test]
    public async Task GroupedAggregateOverHttp()
    {
        var regions = await query.Order
            .GroupBy(_ => _.Region)
            .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
            .ToListAsync();

        var north = regions.Single(_ => _.Region == "North");
        Assert.That(north.Total, Is.EqualTo(350m));
        Assert.That(north.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task CountOverHttp()
    {
        var count = await query.Employee
            .Where(_ => _.Active)
            .CountAsync();

        Assert.That(count, Is.EqualTo(3));
    }

    // begin-snippet: rawRequestRejected
    [Test]
    public async Task DisallowedPropertyRejectedWith400()
    {
        const string json = """
            {
              "version": 1,
              "root": "Employee",
              "pipeline": [
                {
                  "$type": "where",
                  "predicate": {
                    "$type": "binary",
                    "op": "GreaterThan",
                    "left": { "$type": "member", "path": ["Salary"] },
                    "right": { "$type": "const", "value": "100", "tag": "Decimal" }
                  }
                }
              ]
            }
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
    // end-snippet

    // The lockstep guarantee behind stale-client detection: the stamp the generator bakes into the
    // client (computed from Sample.Model's metadata on disk) must equal the stamp the server computes
    // from the same assembly via reflection. If the two surface readers ever diverge, this fails.
    [Test]
    public void GeneratedSchemaStampMatchesServer()
    {
        var processor = app.Services.GetRequiredService<ScryProcessor>();

        Assert.That(ScryQuery.SchemaStamp, Is.EqualTo(processor.Describe().SchemaStamp));
    }

    [Test]
    public async Task ResponseAdvertisesSchemaStamp()
    {
        using var content = new StringContent(
            """{"version":1,"root":"Employee","pipeline":[{"$type":"count"}]}""",
            Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync("/api/query", content);

        Assert.That(
            response.Headers.GetValues("Scry-Schema-Stamp").Single(),
            Is.EqualTo(ScryQuery.SchemaStamp));
    }

    // A client generated against the live model must never report itself stale — this is the
    // in-agreement half of the SchemaStale signal.
    [Test]
    public async Task MatchingClientIsNotReportedStale()
    {
        await query.Employee.CountAsync();

        Assert.That(client.ServerSchemaStamp, Is.EqualTo(ScryQuery.SchemaStamp));
        Assert.That(client.SchemaStale, Is.False);
    }

    // The drifted case: a client carrying a stamp from an older model learns it is stale from a
    // response header, even though the query itself succeeded.
    [Test]
    public async Task DriftedClientIsReportedStale()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();

        Assert.That(stale.SchemaStale, Is.True);
    }

    [Test]
    public async Task DriftedClientRaisesSchemaStaleDetected()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        SchemaDrift? drift = null;
        stale.SchemaStaleDetected += _ => drift = _;

        // The query itself succeeds — drift is reported alongside a working result, not as a failure.
        var count = await stale.Source<EmployeeQueryModel>("Employee").CountAsync();

        Assert.That(count, Is.EqualTo(4));
        Assert.That(drift, Is.Not.Null);
        Assert.That(drift!.ClientStamp, Is.EqualTo("stamp-from-an-older-model"));
        Assert.That(drift.ServerStamp, Is.EqualTo(ScryQuery.SchemaStamp));
    }

    // Raised once per client, however many queries follow: an app that polls would otherwise re-prompt
    // for a reload on every request until the user acts.
    [Test]
    public async Task SchemaStaleDetectedIsRaisedOnce()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        var raised = 0;
        stale.SchemaStaleDetected += _ => raised++;

        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();
        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();
        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();

        Assert.That(raised, Is.EqualTo(1));
    }

    // A client generated against the live model must stay silent — the half that keeps the signal from
    // being noise. Uses its own client so the subscription cannot leak into the shared fixture.
    [Test]
    public async Task MatchingClientNeverRaisesSchemaStaleDetected()
    {
        var current = ScryClient.ForHttp(http, "/api/query");
        var matching = new ScryQuery(current);

        var raised = false;
        current.SchemaStaleDetected += _ => raised = true;

        await matching.Employee.CountAsync();

        Assert.That(raised, Is.False);
        Assert.That(current.SchemaStale, Is.False);
    }

    [Test]
    public void DisallowedPropertyThrowsThroughClient() =>
        // The generated client model has no Salary member (the server marks it [QueryIgnore]), so
        // attempts to reach hidden data must come as raw requests, which the server rejects (see the
        // 400 test). Here we confirm an unknown root is rejected through the typed client path.
        Assert.ThrowsAsync<ScryRequestException>(() =>
            client.Source<EmployeeQueryModel>("Secret").ToListAsync());
}
