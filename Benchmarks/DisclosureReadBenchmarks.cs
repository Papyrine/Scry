using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;
using Scry;

namespace Benchmarks;

/// <summary>
/// What the questions the disclosure record exists for cost to ask of the SQL Server store, over a
/// record of some size: who received a row, what a caller received, whether a caller was sent a
/// member, and an answer put back together.
/// </summary>
/// <remarks>
/// Three records, since how the store keeps units depends on how alike the answers are.
/// <c>Repeated</c> is one answer of a thousand rows given five hundred times. <c>Drifting</c> is five
/// hundred answers of a thousand rows, each one row different from the one before, which is what a
/// live query leaves behind. <c>Small</c> is ten thousand answers of one row each.
/// <para>
/// This is the one benchmark here with a database in it: LocalDB's default instance, in a database
/// named after the record and after the tables the store makes, so that a run against another layout
/// of them fills its own. A record is filled once and found again by every later run. What filling it
/// took and what it weighs are written beside the results, in the system's temporary directory, as
/// <c>scry_bench_disclosure_*.txt</c>.
/// </para>
/// </remarks>
[SimpleJob(warmupCount: 3, iterationCount: 12)]
public class DisclosureReadBenchmarks
{
    const int rows = 1000;
    const int answers = 500;
    const int small = 10_000;
    const int callers = 10;

    static DateTimeOffset start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    static ScryDisclosureShape names = new(
        Address(ScryDisclosureContentKind.Shape, "Patient.Name"),
        [new("Patient", "Name", ScryDisclosureFieldUse.Returned, false)]);

    ScrySqlServerDisclosureStore store = null!;
    Guid last;
    object?[] asked = null!;

    [Params("Repeated", "Drifting", "Small")]
    public string Record { get; set; } = "Repeated";

    [GlobalSetup]
    public async Task Setup()
    {
        var probe = new ScrySqlServerDisclosureStore(new() {ConnectionString = Connection("master"), DrainInterval = null});
        var layout = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(probe.Script)))[..8];
        await probe.DisposeAsync();

        var database = $"ScryDisclosureBench_{Record}_{layout}";
        store = new(new() {ConnectionString = Connection(database), DrainInterval = null});
        var expected = Record == "Small" ? small : answers;
        if (!await Filled(database, expected))
        {
            await Execute("master", $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END; CREATE DATABASE [{database}];");
            var timer = Stopwatch.StartNew();
            await Fill();
            var accepted = timer.Elapsed;
            while (await store.DrainAsync() > 0)
            {
            }

            await Weigh(database, layout, accepted, timer.Elapsed - accepted);
        }

        // Patient 900 is in every large answer, and is never the row a drifting answer changes.
        asked = [Record == "Small" ? 7 : 900];
        var received = new List<ScryDisclosureEntry>();
        await foreach (var entry in store.ReceivedBy("caller-0", DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
        {
            received.Add(entry);
        }

        last = received[0].Event.Id;
    }

    [GlobalCleanup]
    public async Task Cleanup() =>
        await store.DisposeAsync();

    /// <summary>Who received one row: the first page of it, two hundred receipts.</summary>
    [Benchmark]
    public async Task<int> WhoReceivedARow()
    {
        var count = 0;
        await foreach (var _ in store.ReceiversOf("Patient", asked))
        {
            count++;
        }

        return count;
    }

    /// <summary>What one caller received: the first page of its events.</summary>
    [Benchmark]
    public async Task<int> WhatACallerReceived()
    {
        var count = 0;
        await foreach (var _ in store.ReceivedBy("caller-0", DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
        {
            count++;
        }

        return count;
    }

    /// <summary>Whether a caller was sent a member: the first page of the events that sent it.</summary>
    [Benchmark]
    public async Task<int> WasACallerSentAMember()
    {
        var count = 0;
        await foreach (var _ in store.MemberReceivedBy("caller-0", "Patient", "Name"))
        {
            count++;
        }

        return count;
    }

    /// <summary>One answer put back together: every unit in its place, with its content and its rows.</summary>
    [Benchmark]
    public async Task<int> AnAnswerPutBackTogether() =>
        (await store.Reconstruct(last))!.Units.Count;

    async Task Fill()
    {
        if (Record == "Small")
        {
            for (var index = 0; index < small; index++)
            {
                await store.AppendAsync(Answer(index, [1 + index % 100]), default);
            }

            return;
        }

        var patients = Enumerable.Range(1, rows).ToList();
        for (var index = 0; index < answers; index++)
        {
            if (Record == "Drifting" &&
                index > 0)
            {
                // One row of the first half becomes a row never seen before.
                patients[index] = 10_000 + index;
            }

            await store.AppendAsync(Answer(index, patients), default);
        }
    }

    static ScryDisclosureBatch Answer(int index, List<int> patients)
    {
        var id = Guid.CreateVersion7();
        var contents = patients
            .Select(_ => Content($"{{\"name\":\"Patient {_}\",\"ward\":\"Ward {_ % 12}\",\"admitted\":\"2026-02-{1 + _ % 28:00}\"}}"))
            .ToList();
        return new()
        {
            EventId = id,
            Sequence = 0,
            Begin = new(id, start.AddSeconds(index), ScryDisclosureKind.List, "Patient")
            {
                Caller = $"caller-{index % callers}",
                Shape = names.Address,
                Node = "bench"
            },
            Shape = names,
            Units = [.. contents.Select((content, ordinal) => new ScryDisclosureUnit(ordinal, content.Address))],
            Entities = [.. patients.Select((patient, ordinal) => new ScryDisclosureEntity(ordinal, 0, "Patient", $"[{patient}]", ""))],
            Contents = contents,
            Close = new(ScryDisclosureOutcome.Released, patients.Count)
            {
                At = start.AddSeconds(index)
            }
        };
    }

    static ScryDisclosureContent Content(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(Address(ScryDisclosureContentKind.Row, text), ScryDisclosureContentKind.Row, bytes.Length, bytes);
    }

    static ScryDisclosureAddress Address(ScryDisclosureContentKind kind, string text) =>
        ScryDisclosureAddress.From(SHA256.HashData([(byte) kind, .. Encoding.UTF8.GetBytes(text)]));

    static string Connection(string database) =>
        $@"Data Source=(LocalDb)\MSSQLLocalDB;Initial Catalog={database};Integrated Security=True;Encrypt=False";

    static async Task<bool> Filled(string database, int expected)
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
        return (int) (await count.ExecuteScalarAsync())! == expected;
    }

    static async Task Execute(string database, string statement)
    {
        await using var connection = new SqlConnection(Connection(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(statement, connection);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync();
    }

    // What the record weighs, table by table, and what filling it took.
    async Task Weigh(string database, string layout, TimeSpan accepted, TimeSpan moved)
    {
        var report = new StringBuilder();
        report.AppendLine($"{Record} ({layout}): accepted in {accepted.TotalSeconds:0.0} s, moved on in {moved.TotalSeconds:0.0} s");
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
                report.AppendLine($"  {reader.GetString(0),-28} {reader.GetInt64(1),9} rows {reader.GetInt64(2),9} KB");
            }
        }

        report.AppendLine($"  {"all of it",-28} {"",9}      {total,9} KB");
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), $"scry_bench_disclosure_{Record}_{layout}.txt"), report.ToString());
    }
}
