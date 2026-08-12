using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Scry;

namespace Benchmarks;

/// <summary>
/// Reading a response on the client — the counterpart of the writing measured by
/// <see cref="ResponseBenchmarks"/>, and the half that runs in a browser, where the allocations are
/// the ones that matter most.
/// </summary>
/// <remarks>
/// The arms are the two ways the transport can hand a body to the wire reader. <c>Text</c> is a body
/// decoded to a string first, which transcodes the whole response to UTF-16 for the JSON reader to
/// transcode back, and leaves the payload as a <see cref="JsonElement"/> the reader then writes out
/// to a buffer and re-reads to produce the rows. <c>Utf8</c> is the bytes as they arrived, which the
/// response keeps, so the payload is parsed once and straight into the row type.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class ClientReadBenchmarks
{
    public record Row(
        int Id,
        string Name,
        string Region,
        Grade Grade,
        bool Active,
        decimal Amount,
        long Ticks,
        DateTime Created,
        double Score);

    byte[] utf8 = null!;
    string text = null!;

    [Params(1, 100, 1000)]
    public int Rows { get; set; } = 100;

    [GlobalSetup]
    public void Setup()
    {
        var rows = MemRow.Seed(Rows)
            .Select(_ => new Row(_.Id, _.Name, _.Region, _.Grade, _.Active, _.Amount, _.Ticks, _.Created, _.Score));

        var response = QueryResponse.Create(
            ResultKind.List,
            JsonSerializer.SerializeToElement(rows, ScryJson.Options)) with
        {
            Stamp = "8yskMW95UPUIz0wo"
        };

        utf8 = ScryJson.SerializeToUtf8(response);
        text = Encoding.UTF8.GetString(utf8);
    }

    [Benchmark(Baseline = true, Description = "body as a string, payload through a JsonElement")]
    public List<Row>? Text() =>
        ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(text));

    [Benchmark(Description = "body as the utf8 it arrived as")]
    public List<Row>? Utf8() =>
        ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(utf8));
}

/// <summary>
/// Reading one row of a streamed result. A stream's whole point is that neither side holds the result,
/// so the per-row constant is what it costs — and it is paid once per row for as long as the stream
/// runs.
/// </summary>
/// <remarks>
/// <c>Element</c> is a line decoded to a string and parsed into a <see cref="JsonElement"/>, which the
/// reader then writes back out to a buffer and re-reads to produce the row. <c>Utf8</c> reads the
/// line's own bytes straight into the row type. Both resolve enum names and binary placeholders the
/// same way, so the difference is the document in between.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class StreamedRowBenchmarks
{
    byte[] utf8 = null!;
    string text = null!;

    [GlobalSetup]
    public void Setup()
    {
        var row = MemRow.Seed(1).Single();
        utf8 = JsonSerializer.SerializeToUtf8Bytes(
            new ClientReadBenchmarks.Row(
                row.Id, row.Name, row.Region, row.Grade, row.Active, row.Amount, row.Ticks, row.Created, row.Score),
            ScryJson.Options);
        text = Encoding.UTF8.GetString(utf8);
    }

    [Benchmark(Baseline = true, Description = "line as a string, through a JsonElement")]
    public ClientReadBenchmarks.Row? Element() =>
        ScryJson.DeserializeRow<ClientReadBenchmarks.Row>(
            JsonSerializer.Deserialize<JsonElement>(text, ScryJson.Options),
            aliases: null);

    [Benchmark(Description = "line as the utf8 it arrived as")]
    public ClientReadBenchmarks.Row? Utf8() =>
        ScryJson.DeserializeRow<ClientReadBenchmarks.Row>(utf8, aliases: null);
}
