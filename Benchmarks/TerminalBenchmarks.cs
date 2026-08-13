using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scry;

namespace Benchmarks;

/// <summary>
/// The same pair as <see cref="ResponseBenchmarks"/>, for the two results that are not rows: one
/// projected row, and a count. <c>Legacy</c> shapes the row into a dictionary, serializes it into a
/// <c>JsonElement</c>, and serializes the envelope around it a second time; <c>Endpoint</c> writes the
/// row through the same shape writer a list's rows go through, and a scalar through the same value
/// writer a row's leaves do.
/// </summary>
/// <remarks>
/// A terminal costs the same whatever the source holds, so there is no row count to scale and a single
/// request measures almost entirely the fixed cost each arm carries — a loopback round trip on one
/// side, a processor call on the other. Two things put a marginal figure back in reach. The terminals
/// are carried as batch entries, so the growth from 1 entry to 20 divides that constant out and leaves
/// what one terminal costs to shape and write. And the source is kept narrow, because a wider one only
/// adds pipeline work that both arms pay identically and that buries the difference under it.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class TerminalBenchmarks
{
    WebApplication app = null!;
    HttpClient http = null!;
    ScryProcessor processor = null!;
    BenchContext db = null!;
    string requestJson = null!;
    QueryBatchRequest request = null!;

    /// <summary>Single is one projected row of nine members; Scalar is a count.</summary>
    [Params("Single", "Scalar")]
    public string Terminal { get; set; } = "Single";

    [Params(1, 20)]
    public int Entries { get; set; } = 20;

    const int rows = 10;

    [GlobalSetup]
    public async Task Setup()
    {
        var query = Terminal == "Single" ? Requests.Single() : Requests.Scalar();
        request = QueryBatchRequest.Create([..Enumerable.Range(0, Entries).Select(_ => query)]);
        requestJson = ScryJson.Serialize(request);

        db = BenchContext.Create();
        processor = ScryProcessor.Create<BenchContext>(Configure);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<BenchContext>(
            _ => _.UseSqlServer("Server=(localdb)\\scry-benchmarks-never-opens;Database=none"));
        builder.Services.AddScry<BenchContext>(Configure);

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        http = app.GetTestClient();

        // Warm both paths: JIT, the plan cache, and the endpoint's first-request machinery.
        processor.ExecuteBatch(request, db);
        await Post();
    }

    void Configure(ScryOptions options) =>
        options.AddPocoSource(_ => MemRow.Seed(rows));

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        db.Dispose();
    }

    [Benchmark(Baseline = true, Description = "dictionary + JsonElement + serialize")]
    public string Legacy() =>
        ScryJson.Serialize(processor.ExecuteBatch(request, db));

    [Benchmark(Description = "written straight from the projected values (over HTTP)")]
    public Task<string> Endpoint() =>
        Post();

    async Task<string> Post()
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query/batch", content);
        return await response.Content.ReadAsStringAsync();
    }
}
