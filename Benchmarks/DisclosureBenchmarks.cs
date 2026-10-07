using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scry;

namespace Benchmarks;

/// <summary>
/// What the disclosure audit adds to an answer, over the HTTP endpoint and the row writer it shares a
/// buffer with. <c>Off</c> is the endpoint with no audit, and the baseline. <c>Recorded</c> has the
/// audit on over a sink that discards what it is handed, so what it measures is the capture alone —
/// each row hashed and copied into the batch, its key read and written, the batch built.
/// <c>Journaled</c> puts the journal in front of that sink, so it adds what a durable accept on this
/// machine costs: a write and a flush to disk before the answer is sent.
/// </summary>
/// <remarks>
/// The bytes sent are the same in all three, which <c>DisclosureTests</c> pins against the fast
/// writer's own corpus. The journaled arm's time is the disk's: it is the one figure here that says
/// more about the machine it ran on than about the code.
/// <para>
/// <c>Off</c> is the request <c>ResponseBenchmarks.Endpoint</c> sends, from a host that writes no
/// logs. That a host with the audit off pays nothing for it is not something this arm can say by
/// itself, since it runs the code as it now is: it is what that arm reads on the commit before the
/// audit existed against what it reads after, and <c>docs/performance.md</c> records both.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class DisclosureBenchmarks
{
    WebApplication off = null!;
    WebApplication recorded = null!;
    WebApplication journaled = null!;
    HttpClient offClient = null!;
    HttpClient recordedClient = null!;
    HttpClient journaledClient = null!;
    string directory = null!;
    string requestJson = null!;

    [Params(1, 100, 1000)]
    public int Rows { get; set; } = 100;

    [GlobalSetup]
    public async Task Setup()
    {
        requestJson = ScryJson.Serialize(Requests.Wide());
        directory = Directory.CreateTempSubdirectory("scry_bench_journal_").FullName;

        off = await Start(_ => { });
        recorded = await Start(_ => _.UseDisclosureAudit(new Discard(), Rowed));
        journaled = await Start(
            _ => _.UseDisclosureAudit(
                new Discard(),
                audit =>
                {
                    Rowed(audit);
                    audit.UseJournal(directory);
                }));
        offClient = off.GetTestClient();
        recordedClient = recorded.GetTestClient();
        journaledClient = journaled.GetTestClient();

        // Warm every arm: JIT, the plan cache, and the endpoint's first-request machinery. And check
        // that the arms are what they say: the same bytes, recorded in two of them and not the third.
        var plain = await Post(offClient);
        if (await Post(recordedClient) != plain ||
            await Post(journaledClient) != plain)
        {
            throw new("The audit changed the bytes of a response, so the arms are not measuring the same answer.");
        }
    }

    // A row is recorded by its key, which costs a slot read and a few bytes written for each: the
    // realistic case, and the dearer one.
    static void Rowed(ScryDisclosureOptions audit) =>
        audit.Key<MemRow>(_ => _.Id);

    async Task<WebApplication> Start(Action<ScryOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // A host writes a line or two to the console for every request it answers: a few kilobytes of
        // allocation each time, where recording adds under 8 KB to a one-row answer. Left on, the
        // logging would be a good part of the smallest difference this benchmark is here to show.
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<BenchContext>(
            _ => _.UseSqlServer("Server=(localdb)\\scry-benchmarks-never-opens;Database=none"));
        builder.Services.AddScry<BenchContext>(
            options =>
            {
                options.AddPocoSource(_ => MemRow.Seed(Rows));
                options.Caller = _ => "bench";
                configure(options);
            });

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var app in new[] {off, recorded, journaled})
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        offClient.Dispose();
        recordedClient.Dispose();
        journaledClient.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [Benchmark(Baseline = true, Description = "audit off")]
    public Task<string> Off() =>
        Post(offClient);

    [Benchmark(Description = "recorded: each row hashed and keyed, handed to a sink that discards it")]
    public Task<string> Recorded() =>
        Post(recordedClient);

    [Benchmark(Description = "recorded through a journal: flushed to disk before it is sent")]
    public Task<string> Journaled() =>
        Post(journaledClient);

    async Task<string> Post(HttpClient http)
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);
        return await response.Content.ReadAsStringAsync();
    }

    // A sink that accepts everything and keeps nothing, so that what is measured is what leads up to
    // the accept and not a store.
    sealed class Discard :
        IScryDisclosureSink
    {
        public void Append(ScryDisclosureBatch batch)
        {
        }

        public ValueTask AppendAsync(ScryDisclosureBatch batch, CancellationToken cancel) =>
            ValueTask.CompletedTask;
    }
}
