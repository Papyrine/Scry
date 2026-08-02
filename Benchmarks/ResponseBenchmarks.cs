using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scry;

namespace Benchmarks;

/// <summary>
/// Producing the complete response bytes, two ways. <c>Legacy</c> is
/// <see cref="ScryProcessor.Execute(QueryRequest, DbContext)"/> plus <c>ScryJson.Serialize</c> — the
/// path a non-HTTP transport still takes: a dictionary per row, a <c>JsonElement</c> payload, then
/// the envelope serialized a second time. <c>Endpoint</c> is the HTTP endpoint, which writes rows
/// straight from the projected values. The two produce byte-identical output (pinned by
/// <c>FastWriterGoldenTests</c>).
/// </summary>
/// <remarks>
/// Only the endpoint arm pays HTTP framing and a loopback round trip, so its absolute numbers carry
/// a constant the other does not — read the <b>marginal</b> cost instead: the growth from 1 row to
/// 1000 is the shaping-and-serialization cost each path actually adds per row.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class ResponseBenchmarks
{
    WebApplication app = null!;
    HttpClient http = null!;
    ScryProcessor processor = null!;
    BenchContext db = null!;
    string requestJson = null!;
    QueryRequest request = null!;

    [Params(1, 100, 1000)]
    public int Rows { get; set; } = 100;

    [GlobalSetup]
    public async Task Setup()
    {
        request = Requests.Wide();
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
        processor.Execute(request, db);
        await Post();
    }

    void Configure(ScryOptions options) =>
        options.AddPocoSource(_ => MemRow.Seed(Rows));

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
        ScryJson.Serialize(processor.Execute(request, db));

    [Benchmark(Description = "written straight from projected rows (over HTTP)")]
    public Task<string> Endpoint() =>
        Post();

    async Task<string> Post()
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);
        return await response.Content.ReadAsStringAsync();
    }
}
