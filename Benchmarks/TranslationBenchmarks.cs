using BenchmarkDotNet.Attributes;
using Scry;

namespace Benchmarks;

/// <summary>
/// Translating a captured query into the wire request — the client's per-send cost, and the one that
/// runs on the interpreter a browser gives a WASM client.
/// </summary>
/// <remarks>
/// The arms differ only in the shape of their closure state. <c>Captured</c> is a plain variable,
/// which the reader has always read directly; the rest are the shapes that used to reach
/// <c>Expression.Lambda(…).Compile()</c> — a method call, arithmetic, a constructed value — and each
/// compile builds a delegate that is invoked once and discarded.
///
/// The queryables are built once, so what is measured is the translation rather than the expression
/// tree the C# compiler rebuilds per statement.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class TranslationBenchmarks
{
    static readonly string[] projection = ["Id", "Name", "Region", "Amount"];

    IQueryable<MemRow> captured = null!;
    IQueryable<MemRow> calls = null!;
    IQueryable<MemRow> arithmetic = null!;
    IQueryable<MemRow> constructed = null!;
    IQueryable<MemRow> mixed = null!;

    [GlobalSetup]
    public void Setup()
    {
        var client = new ScryClient(
            (_, _) => throw new("These benchmarks translate the query; they do not send it."));
        var source = client.Source<MemRow>("MemRow", projection);

        var region = "North";
        var threshold = 10.5m;
        var page = 3;
        var size = 20;
        var stamped = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A plain captured variable, and a member read off one: what the reader already answered
        // without compiling, so this arm is the control.
        captured = source
            .Where(_ => _.Region == region && _.Amount > threshold)
            .Skip(page)
            .Take(size);

        // Calls over closure state. Relative dates and formatted text are the everyday spelling, and
        // every one of them used to cost a compile.
        calls = source
            .Where(_ =>
                _.Created > DateTime.UtcNow.AddDays(-7) &&
                _.Region == region.PadLeft(8) &&
                _.Score > Math.Clamp(1.5, 0, 2));

        // Arithmetic over closure state, which is how a page offset is usually written.
        arithmetic = source
            .Where(_ => _.Amount > threshold * 2 && _.Ticks < (long) size * 1000)
            .Skip(page * size)
            .Take(size + 1);

        // Values the closure constructs: an array tested for membership, and a date built by hand.
        constructed = source
            .Where(_ => new[] {1, 2, 3, 5, 8}.Contains(_.Id) && _.Created > new DateTime(2026, 3, 4));

        // The shapes together, as one query would spell them.
        mixed = source
            .Where(_ =>
                new[] {"North", "South"}.Contains(_.Region) &&
                _.Created > stamped.AddMonths(-1) &&
                _.Amount > threshold * 2)
            .Skip(page * size)
            .Take(size);
    }

    [Benchmark(Baseline = true, Description = "captured variables only")]
    public QueryRequest Captured() =>
        captured.ToScryRequest();

    [Benchmark(Description = "calls over closure state")]
    public QueryRequest Calls() =>
        calls.ToScryRequest();

    [Benchmark(Description = "arithmetic over closure state")]
    public QueryRequest Arithmetic() =>
        arithmetic.ToScryRequest();

    [Benchmark(Description = "constructed closure values")]
    public QueryRequest Constructed() =>
        constructed.ToScryRequest();

    [Benchmark(Description = "the shapes together")]
    public QueryRequest Mixed() =>
        mixed.ToScryRequest();
}
