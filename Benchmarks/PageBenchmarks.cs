using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scry;

namespace Benchmarks;

/// <summary>
/// Producing a page response, two ways — the paging counterpart of <see cref="ResponseBenchmarks"/>.
/// <c>Legacy</c> is <see cref="ScryProcessor.Execute(QueryRequest, DbContext)"/> plus
/// <c>ScryJson.Serialize</c>: a dictionary per row, the whole envelope serialized into a
/// <c>JsonElement</c>, then serialized a second time to produce the bytes. <c>Endpoint</c> is the HTTP
/// endpoint, which writes the page's rows straight from the projected values through the same shape
/// writer a list's rows go through. The two produce byte-identical output, pinned by the
/// <c>page envelope</c> case in <c>FastWriterGoldenTests</c>.
/// </summary>
/// <remarks>
/// Read the marginal cost, as in <see cref="ResponseBenchmarks"/>: only the endpoint arm pays HTTP
/// framing and a loopback round trip, so the growth from a small page to a large one is what each
/// path actually adds per row.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class PageBenchmarks
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
        request = Requests.Page(Rows);
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

        processor.Execute(request, db);
        await Post();
    }

    // A page is capped by MaxPageSize, so the cap has to clear the largest page measured or the
    // biggest arm would quietly measure a smaller one. Seeded past the page size so every page has a
    // further one, which is the case that also mints a cursor.
    void Configure(ScryOptions options)
    {
        options.MaxPageSize = 2000;
        options.AddPocoSource(_ => MemRow.Seed(Rows + 1));
    }

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
