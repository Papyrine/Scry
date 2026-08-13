using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scry;

namespace Benchmarks;

/// <summary>
/// The same pair as <see cref="ResponseBenchmarks"/>, for a batch. <c>Legacy</c> is
/// <see cref="ScryProcessor.ExecuteBatch(QueryBatchRequest, DbContext)"/> plus <c>ScryJson.Serialize</c>
/// — a dictionary per row and a <c>JsonElement</c> per entry, then one pass over the envelope that
/// serializes every one of those elements a second time. <c>Endpoint</c> is the HTTP batch endpoint,
/// which writes each entry's rows straight from the projected values into the envelope. The two
/// produce byte-identical output (pinned by <c>FastWriterGoldenTests.BatchBytesMatchTheGeneralPath</c>).
/// </summary>
/// <remarks>
/// Rows are held at a hundred — around where the single-response crossover sits — and the entry count
/// varied instead, because what a batch adds is the same per-entry work repeated. Only the endpoint arm
/// pays HTTP framing and a loopback round trip, and it pays it <b>once</b> for the whole batch however
/// many entries it carries, which is the constant that shrinks per entry as the batch grows.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class BatchBenchmarks
{
    WebApplication app = null!;
    HttpClient http = null!;
    ScryProcessor processor = null!;
    BenchContext db = null!;
    string requestJson = null!;
    QueryBatchRequest request = null!;

    [Params(1, 5, 20)]
    public int Entries { get; set; } = 5;

    const int rows = 100;

    [GlobalSetup]
    public async Task Setup()
    {
        request = QueryBatchRequest.Create([..Enumerable.Range(0, Entries).Select(_ => Requests.Wide())]);
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

    [Benchmark(Baseline = true, Description = "dictionaries + JsonElement + serialize")]
    public string Legacy() =>
        ScryJson.Serialize(processor.ExecuteBatch(request, db));

    [Benchmark(Description = "written straight from projected rows (over HTTP)")]
    public Task<string> Endpoint() =>
        Post();

    async Task<string> Post()
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query/batch", content);
        return await response.Content.ReadAsStringAsync();
    }
}
