using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Skry;
using Skry.Client;

namespace IntegrationTests;

// Client-side query models (what a generated client would expose). Distinct from the server types.
public enum Status
{
    FullTime,
    PartTime,
    Contractor
}

public class Department
{
    public string Name { get; set; } = "";
}

public class Employee
{
    public string Name { get; set; } = "";
    public Status Status { get; set; }
    public bool Active { get; set; }
    public Employee? Manager { get; set; }
    public Department? Department { get; set; }
}

public class Order
{
    public string Region { get; set; } = "";
    public decimal Amount { get; set; }
}

[TestFixture]
public class HttpRoundTripTests
{
    WebApplication app = null!;
    HttpClient http = null!;
    SkryClient client = null!;
    string dbPath = null!;

    record EmployeeRow(string Name, Status Status, string? Manager, string Department);

    record RegionSummary(string Region, decimal Total, int Count);

    static readonly string[] activeEmployeeNames = ["Aaron", "Alice", "Carol"];

    [OneTimeSetUp]
    public async Task StartServer()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"skry_it_{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddSkry(options =>
        {
            options.UseModel<Sample.Model.SampleContext>();
            options.AddPocoSource<Sample.Model.Holiday>(_ => Sample.Model.Holiday.Seed());
            options.MaxPageSize = 200;
        });

        app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            Sample.Model.SampleContext.Initialize(scope.ServiceProvider.GetRequiredService<Sample.Model.SampleContext>());
        }

        app.MapSkry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        client = SkryClient.ForHttp(http, "/api/query");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();

        // The SQLite connection pool may still hold the temp file; release it before deleting.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    [Test]
    public async Task EmployeesProjectionOverHttp()
    {
        var rows = await client.Source<Employee>("Employee")
            .Where(e => e.Active)
            .OrderBy(e => e.Name)
            .Select(e => new EmployeeRow(e.Name, e.Status, e.Manager!.Name, e.Department!.Name))
            .ToSkryListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(activeEmployeeNames));
        Assert.That(rows[0].Manager, Is.EqualTo("Alice"));
        Assert.That(rows[1].Manager, Is.Null);
        Assert.That(rows[0].Department, Is.EqualTo("Engineering"));
    }

    [Test]
    public async Task GroupedAggregateOverHttp()
    {
        var regions = await client.Source<Order>("Order")
            .GroupBy(o => o.Region)
            .Select(g => new RegionSummary(g.Key, g.Sum(x => x.Amount), g.Count()))
            .ToSkryListAsync();

        var north = regions.Single(_ => _.Region == "North");
        Assert.That(north.Total, Is.EqualTo(350m));
        Assert.That(north.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task CountOverHttp()
    {
        var count = await client.Source<Employee>("Employee")
            .Where(e => e.Active)
            .CountSkryAsync();

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
    public void DisallowedPropertyThrowsThroughClient()
    {
        // The client cannot even name Salary (no such member on the client model), so attempts to
        // reach hidden data must come as raw requests, which the server rejects (see the 400 test).
        // Here we confirm an unknown root is rejected through the typed client path.
        Assert.ThrowsAsync<SkryRequestException>(() =>
            client.Source<Employee>("Secret").ToSkryListAsync());
    }
}
