using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;
using Scry;

namespace Benchmarks;

/// <summary>
/// The questions <see cref="DisclosureReadBenchmarks"/> asks, of a record the size a busy host makes:
/// a million answers, from a thousand callers, over a hundred thousand rows, kept in one segment or
/// in many, asked with the database idle and with another record being written beside it.
/// </summary>
/// <remarks>
/// <para>
/// An answer is one to ten rows of the hundred thousand, picked by its number, so every row is in
/// some fifty answers. One row, patient 7, is in every hundredth answer as well, ten thousand in all,
/// which is the row asked about: who received it is a question with pages to spare. Each caller has a
/// thousand answers. They are a second apart, so the record covers some eleven and a half days.
/// </para>
/// <para>
/// <c>Writing</c> is the database with other work to do: for the whole of every measurement, a
/// second store in the same database accepts answers as fast as four writers can hand them over and
/// moves them on as it goes. It writes a schema of its own, so the record measured stays the size it
/// was filled to, while the database, its log and its memory are shared. How many answers it took
/// while a case ran is written beside the results.
/// </para>
/// <para>
/// A record is filled once, in a database named after its size, its segments and the tables the
/// store makes, and found again by every later run. Filling the three takes a while: what each took
/// and what it weighs are written to <c>scry_bench_scale_*.txt</c> in the system's temporary
/// directory. <c>SCRY_BENCH_ANSWERS</c> fills a smaller record, for trying the benchmark out.
/// </para>
/// </remarks>
[SimpleJob(warmupCount: 3, iterationCount: 12)]
public class DisclosureScaleBenchmarks
{
    const int patients = 100_000;
    const int callers = 1000;
    const int hot = 7;

    // What the store reads a listing in, and so what a first page is.
    const int page = 200;

    static int answers = int.TryParse(Environment.GetEnvironmentVariable("SCRY_BENCH_ANSWERS"), out var asked) ? asked : 1_000_000;

    static DateTimeOffset start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    static ScryDisclosureShape names = new(
        Address(ScryDisclosureContentKind.Shape, "Patient.Name"),
        [new("Patient", "Name", ScryDisclosureFieldUse.Returned, false)]);

    ScrySqlServerDisclosureStore store = null!;
    ScrySqlServerDisclosureStore? busy;
    CancellationTokenSource? stopping;
    Task? writing;
    long written;
    string report = "";
    Guid last;
    DateTimeOffset lately;

    /// <summary>How many segments the record is kept in, the answers shared out evenly in order.</summary>
    [Params(1, 6, 24)]
    public int Segments { get; set; } = 1;

    /// <summary>Whether the database has nothing else to do, or is taking another record as well.</summary>
    [Params("Idle", "Writing")]
    public string Load { get; set; } = "Idle";

    [GlobalSetup]
    public async Task Setup()
    {
        var probe = new ScrySqlServerDisclosureStore(new() {ConnectionString = Connection("master"), DrainInterval = null});
        var layout = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(probe.Script)))[..8];
        await probe.DisposeAsync();

        var database = $"ScryDisclosureScale_{answers}_{Segments}_{layout}";
        report = Path.Combine(Path.GetTempPath(), $"scry_bench_scale_{answers}_{Segments}_{layout}.txt");
        store = new(new() {ConnectionString = Connection(database), DrainInterval = null, CommandTimeout = TimeSpan.FromMinutes(10)});
        if (!await Filled(database))
        {
            await Execute("master", $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END; CREATE DATABASE [{database}];");
            await Execute("master", $"ALTER DATABASE [{database}] SET RECOVERY SIMPLE;");
            var timer = Stopwatch.StartNew();
            await Fill();
            await Weigh(database, layout, timer.Elapsed);
        }

        var received = new List<ScryDisclosureEntry>();
        await foreach (var entry in store.ReceivedBy("caller-0", DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
        {
            received.Add(entry);
        }

        last = received[0].Event.Id;
        lately = start.AddSeconds(answers * 0.95);

        if (Load == "Writing")
        {
            busy = new(new() {ConnectionString = Connection(database), DrainInterval = null, Schema = "busy"});
            await busy.EnsureCreatedAsync();
            stopping = new();
            writing = Write(busy, stopping.Token);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (stopping is not null)
        {
            await stopping.CancelAsync();
            await writing!;
            await File.AppendAllTextAsync(report, $"  {Load}: the other record took {written} answers while this case ran{Environment.NewLine}");
            await busy!.DisposeAsync();
        }

        await store.DisposeAsync();
    }

    /// <summary>Who received one row: the first page of its ten thousand receipts.</summary>
    [Benchmark]
    public Task<int> WhoReceivedARow() =>
        FirstPage(store.ReceiversOf("Patient", [hot]));

    /// <summary>Every one of the row's ten thousand receipts, a page at a time: what an export reads.</summary>
    [Benchmark]
    public async Task<int> EveryReceiptOfARow()
    {
        var count = 0;
        await foreach (var _ in store.ReceiversOf("Patient", [hot]))
        {
            count++;
        }

        return count;
    }

    /// <summary>What one caller received: the first page of its thousand answers.</summary>
    [Benchmark]
    public Task<int> WhatACallerReceived() =>
        FirstPage(store.ReceivedBy("caller-0", DateTimeOffset.MinValue, DateTimeOffset.MaxValue));

    /// <summary>Who received one row in the last twentieth of the time the record covers: the first page.</summary>
    [Benchmark]
    public Task<int> WhoReceivedARowLately() =>
        FirstPage(store.ReceiversOf("Patient", [hot], lately));

    /// <summary>What one caller received in that same last twentieth: the first page.</summary>
    [Benchmark]
    public Task<int> WhatACallerReceivedLately() =>
        FirstPage(store.ReceivedBy("caller-0", lately, DateTimeOffset.MaxValue));

    /// <summary>Whether a caller was sent a member: the first page of the answers that sent it.</summary>
    [Benchmark]
    public Task<int> WasACallerSentAMember() =>
        FirstPage(store.MemberReceivedBy("caller-0", "Patient", "Name"));

    // A listing is read a page at a time, and asking for more than a page asks for the next one: a
    // question as a screen asks it is its first page and no more.
    static async Task<int> FirstPage<T>(IAsyncEnumerable<T> items)
    {
        var count = 0;
        await foreach (var _ in items)
        {
            if (++count == page)
            {
                break;
            }
        }

        return count;
    }

    /// <summary>One answer put back together.</summary>
    [Benchmark]
    public async Task<int> AnAnswerPutBackTogether() =>
        (await store.Reconstruct(last))!.Units.Count;

    // Each segment's share of the answers, accepted by eight writers at once and moved on a slice at
    // a time, so the outbox never holds more than a slice; then the next segment is started.
    async Task Fill()
    {
        const int slice = 20_000;
        var share = answers / Segments;
        for (var segment = 0; segment < Segments; segment++)
        {
            var first = segment * share;
            var end = segment == Segments - 1 ? answers : first + share;
            for (var from = first; from < end; from += slice)
            {
                var to = Math.Min(end, from + slice);
                await Parallel.ForAsync(from, to, new ParallelOptions {MaxDegreeOfParallelism = 8}, async (index, cancel) => await store.AppendAsync(Answer(index), cancel));
                while (await store.DrainAsync() > 0)
                {
                }
            }

            if (segment < Segments - 1)
            {
                await store.RollAsync();
            }
        }
    }

    // The other work: four writers accepting answers into the other record, and the record moved on
    // between their rounds, until the case is over.
    async Task Write(ScrySqlServerDisclosureStore other, Cancel cancel)
    {
        var index = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var round = index;
                await Parallel.ForAsync(round, round + 40, new ParallelOptions {MaxDegreeOfParallelism = 4, CancellationToken = cancel}, async (at, token) => await other.AppendAsync(Answer(at), token));
                index += 40;
                Interlocked.Add(ref written, 40);
                await other.DrainAsync(cancel);
            }
        }
        // Stopped part-way through a command, which the driver reports as a failure of the command.
        catch (Exception) when (cancel.IsCancellationRequested)
        {
        }
    }

    static ScryDisclosureBatch Answer(int index)
    {
        var count = 1 + index % 10;
        var rows = new List<int>(count + 1);
        if (index % 100 == 0)
        {
            rows.Add(hot);
        }

        for (var at = 0; rows.Count < count; at++)
        {
            var patient = 1 + (int) ((index * 7919L + at * 104_729L) % patients);
            if (patient != hot &&
                !rows.Contains(patient))
            {
                rows.Add(patient);
            }
        }

        var id = Guid.CreateVersion7();
        var contents = rows.Select(_ => Content(Row(_))).ToList();
        var given = start.AddSeconds(index);
        return new()
        {
            EventId = id,
            Sequence = 0,
            Begin = new(id, given, ScryDisclosureKind.List, "Patient")
            {
                Caller = $"caller-{index % callers}",
                Shape = names.Address,
                Node = "bench"
            },
            Shape = names,
            Units = [.. contents.Select((content, ordinal) => new ScryDisclosureUnit(ordinal, content.Address))],
            Entities = [.. rows.Select((patient, ordinal) => new ScryDisclosureEntity(ordinal, 0, "Patient", $"[{patient}]", ""))],
            Contents = contents,
            Close = new(ScryDisclosureOutcome.Released, rows.Count)
            {
                At = given
            }
        };
    }

    static string Row(int patient) =>
        $"{{\"name\":\"Patient {patient}\",\"ward\":\"Ward {patient % 12}\",\"admitted\":\"2026-02-{1 + patient % 28:00}\"}}";

    static ScryDisclosureContent Content(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(Address(ScryDisclosureContentKind.Row, text), ScryDisclosureContentKind.Row, bytes.Length, bytes);
    }

    static ScryDisclosureAddress Address(ScryDisclosureContentKind kind, string text) =>
        ScryDisclosureAddress.From(SHA256.HashData([(byte) kind, .. Encoding.UTF8.GetBytes(text)]));

    static string Connection(string database) =>
        $@"Data Source=(LocalDb)\MSSQLLocalDB;Initial Catalog={database};Integrated Security=True;Encrypt=False";

    static async Task<bool> Filled(string database)
    {
        await using var connection = new SqlConnection(Connection("master"));
        await connection.OpenAsync();
        await using var exists = new SqlCommand($"SELECT DB_ID(N'{database}')", connection);
        if (await exists.ExecuteScalarAsync() is DBNull)
        {
            return false;
        }

        await using var count = new SqlCommand(
            $"IF OBJECT_ID(N'[{database}].[scry].[DisclosureEvent]') IS NULL SELECT 0 ELSE SELECT COUNT(*) FROM [{database}].[scry].[DisclosureEvent]",
            connection);
        count.CommandTimeout = 0;
        return (int) (await count.ExecuteScalarAsync())! == answers;
    }

    static async Task Execute(string database, string statement)
    {
        await using var connection = new SqlConnection(Connection(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(statement, connection);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync();
    }

    async Task Weigh(string database, string layout, TimeSpan filled)
    {
        var text = new StringBuilder();
        text.AppendLine($"{answers} answers in {Segments} segment(s) ({layout}): filled in {filled.TotalMinutes:0.0} min");
        await using var connection = new SqlConnection(Connection(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT o.[name], SUM(CASE WHEN p.[index_id] < 2 THEN p.[row_count] ELSE 0 END), SUM(p.[reserved_page_count]) * 8
            FROM sys.dm_db_partition_stats p JOIN sys.objects o ON o.[object_id] = p.[object_id]
            WHERE o.[name] LIKE N'Disclosure%' AND o.[type] = 'U'
            GROUP BY o.[name] ORDER BY o.[name]
            """,
            connection);
        long total = 0;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                total += reader.GetInt64(2);
                text.AppendLine($"  {reader.GetString(0),-28} {reader.GetInt64(1),11} rows {reader.GetInt64(2),11} KB");
            }
        }

        text.AppendLine($"  {"all of it",-28} {"",11}      {total,11} KB");
        await File.WriteAllTextAsync(report, text.ToString());
    }
}
