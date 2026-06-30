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

    static readonly string[] activeEmployeeNames = ["Aaron", "Alice", "Carol"];

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
            .Where(e => e.Active)
            .OrderBy(e => e.Name)
            .Select(e => new EmployeeRow(e.Name, e.Status, e.Manager!.Name, e.Department!.Name))
            .ToScryListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(activeEmployeeNames));
        Assert.That(rows[0].Manager, Is.EqualTo("Alice"));
        Assert.That(rows[1].Manager, Is.Null);
        Assert.That(rows[0].Department, Is.EqualTo("Engineering"));
    }

    [Test]
    public async Task GroupedAggregateOverHttp()
    {
        var regions = await query.Order
            .GroupBy(o => o.Region)
            .Select(g => new RegionSummary(g.Key, g.Sum(x => x.Amount), g.Count()))
            .ToScryListAsync();

        var north = regions.Single(_ => _.Region == "North");
        Assert.That(north.Total, Is.EqualTo(350m));
        Assert.That(north.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task CountOverHttp()
    {
        var count = await query.Employee
            .Where(e => e.Active)
            .CountScryAsync();

        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public async Task DisallowedPropertyRejectedWith400()
    {
        const string json = """
            {"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"binary","op":"GreaterThan","left":{"$type":"member","path":["Salary"]},"right":{"$type":"const","value":"100","tag":"Decimal"}}}]}
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void DisallowedPropertyThrowsThroughClient() =>
        // The generated client model has no Salary member (the server marks it [QueryIgnore]), so
        // attempts to reach hidden data must come as raw requests, which the server rejects (see the
        // 400 test). Here we confirm an unknown root is rejected through the typed client path.
        Assert.ThrowsAsync<ScryRequestException>(() =>
            client.Source<EmployeeQueryModel>("Secret").ToScryListAsync());
}
