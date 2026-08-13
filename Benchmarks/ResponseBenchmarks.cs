using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scry;

namespace Benchmarks;

/// <summary>
/// Producing the complete response bytes, three ways. <c>Legacy</c> is
/// <see cref="ScryProcessor.Execute(QueryRequest, DbContext)"/> plus <c>ScryJson.Serialize</c> — the
/// path a non-HTTP transport still takes: a dictionary per row, a <c>JsonElement</c> payload, then
/// the envelope serialized a second time. <c>Endpoint</c> is the HTTP endpoint, which writes rows
/// straight from the projected values. The two produce byte-identical output (pinned by
/// <c>FastWriterGoldenTests</c>). <c>Drifted</c> is the endpoint answering a client whose schema
/// stamp disagrees: the one case the row writer declines, so it is the <c>Legacy</c> path reached
/// over HTTP, and it measures what a rejected fast path costs.
/// </summary>
/// <remarks>
/// Only the endpoint arms pay HTTP framing and a loopback round trip, so their absolute numbers carry
/// a constant the first does not — read the <b>marginal</b> cost instead: the growth from 1 row to
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
    string driftedJson = null!;
    QueryRequest request = null!;

    [Params(1, 100, 1000)]
    public int Rows { get; set; } = 100;

    [GlobalSetup]
    public async Task Setup()
    {
        request = Requests.Wide();
        requestJson = ScryJson.Serialize(request);
        // The same query from a client generated against a different model surface. Nothing about the
        // query changes — only that the server answers it the general way and attaches its alias table.
        driftedJson = ScryJson.Serialize(request with {Stamp = "not-this-server's-stamp"});

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

        // Warm every path: JIT, the plan cache, and the endpoint's first-request machinery.
        processor.Execute(request, db);
        await Post(requestJson);

        // The drifted arm only measures what it claims to if the server actually declined the fast
        // path, and the alias table on the envelope is the visible sign that it did. A silent miss
        // would leave this arm quietly measuring the fast path against itself.
        var probe = await Post(driftedJson);
        if (!probe.Contains("enumAliases"))
        {
            throw new(
                "The drifted request was answered by the fast writer, so the arm is measuring the wrong path. The general path needs both a mismatched stamp and a non-empty alias table — check that Grade still carries [PreviousNames].");
        }
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
        Post(requestJson);

    [Benchmark(Description = "the fast path declined: drifted client (over HTTP)")]
    public Task<string> Drifted() =>
        Post(driftedJson);

    async Task<string> Post(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);
        return await response.Content.ReadAsStringAsync();
    }
}
