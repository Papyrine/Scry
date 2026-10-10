/// <summary>
/// What the SQL store's tests share: a store over a test's own database, a processor that records to
/// whatever it is handed, and the text two stores are compared by.
/// </summary>
static class SqlStores
{
    static TimeSpan patience = TimeSpan.FromSeconds(60);

    public static ScrySqlServerDisclosureStore Store(SqlDatabase<ClinicContext> database, Action<ScrySqlServerDisclosureOptions>? configure = null)
    {
        var options = new ScrySqlServerDisclosureOptions
        {
            ConnectionString = database.ConnectionString,

            // Moved on when a test says so, so that what it then reads is what it expects to be there.
            DrainInterval = null
        };
        configure?.Invoke(options);
        return new(options);
    }

    public static ScryProcessor Processor(IScryDisclosureSink sink, Who who) =>
        ScryProcessor.Create<ClinicContext>(
            _ => _.UseDisclosureAudit(
                sink,
                audit =>
                {
                    audit.Caller = _ => who.Name;
                    audit.Node = "ward-1";
                    audit.Unkeyed<Note>();
                    audit.Key<WardCensus>(_ => _.Ward);
                }));

    // Waits for a store that moves batches on by itself to have nothing left to move.
    public static async Task Emptied(ScrySqlServerDisclosureStore store)
    {
        var started = DateTimeOffset.UtcNow;
        while ((await store.Status()).Pending > 0)
        {
            if (DateTimeOffset.UtcNow - started > patience)
            {
                Assert.Fail("The outbox was not moved on.");
            }

            await Task.Delay(50);
        }
    }

    // Waits for something a store does in its own time to have come about.
    public static async Task Until(Func<Task<bool>> reached, string otherwise)
    {
        var started = DateTimeOffset.UtcNow;
        while (!await reached())
        {
            if (DateTimeOffset.UtcNow - started > patience)
            {
                Assert.Fail(otherwise);
            }

            await Task.Delay(50);
        }
    }

    static (string Source, object?[] Key)[] rows =
    [
        ("Patient", [1]),
        ("Patient", [2]),
        ("Patient", [3]),
        ("Ward", [1]),
        ("Admission", [1, 2]),
        ("WardCensus", ["North"])
    ];

    static (string Source, string Member)[] members =
    [
        ("Patient", "Name"),
        ("Patient", "Diagnosis"),
        ("Patient", "WardId"),
        ("Ward", "Name"),
        ("Note", "Text"),
        ("Admission", "PatientId")
    ];

    /// <summary>
    /// Everything a store says about the clinic, as text two stores can be compared by: the three
    /// questions for each caller, every event rebuilt, the catalog, and what was erased. No ids and
    /// no times, so that the same answers recorded twice read the same.
    /// </summary>
    public static async Task<string> Told(IScryDisclosureReader reader, params string?[] callers)
    {
        if (callers.Length == 0)
        {
            callers = ["dr.osei", "nurse.kim"];
        }

        var told = new StringBuilder();
        var events = new List<ScryDisclosureEntry>();
        foreach (var caller in callers)
        {
            var received = await reader.ReceivedBy(caller, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
            events.AddRange(received);
            told.AppendLine($"{Named(caller)} received {received.Count}: {string.Join(", ", received.Select(_ => $"{_.Event.Kind} of {_.Event.Source} ({Ended(_.Close)})"))}");
            foreach (var (source, member) in members)
            {
                var sent = await reader.MemberReceivedBy(caller, source, member).ToListAsync();
                told.AppendLine($"  {source}.{member}: {sent.Count}");
            }
        }

        foreach (var (source, key) in rows)
        {
            var received = await reader.ReceiversOf(source, key).ToListAsync();
            told.AppendLine($"{source}{ScryDisclosureEntity.KeyOf(key)}: {string.Join(", ", received.Select(Receiver))}");
        }

        foreach (var entry in events.OrderBy(_ => _.Event.At))
        {
            var answer = (await reader.Reconstruct(entry.Event.Id))!;
            told.AppendLine($"{answer.Event.Kind} of {answer.Event.Source} for {Named(answer.Event.Caller)} on {answer.Event.Node}, sensitive {answer.Event.Sensitive}, {Ended(answer.Close)}");
            if (answer.Request is { } request)
            {
                told.AppendLine($"  asked {Encoding.UTF8.GetString(request.Span)}");
            }

            told.AppendLine($"  read {string.Join("; ", answer.Shape?.Fields.Select(_ => $"{_.Source}.{_.Member} {_.Use} {_.Sensitive}") ?? [])}");
            foreach (var unit in answer.Units)
            {
                told.AppendLine($"  {unit.Ordinal}: {Sent(unit)} <- {string.Join(", ", unit.Entities.Select(_ => $"{_.Source}{_.Key} via '{_.Via}'"))}");
            }
        }

        var catalog = await reader.Catalog();
        foreach (var source in catalog.Sources)
        {
            told.AppendLine($"{source.Name} keyed {source.Keyed}: {string.Join(", ", source.Members.Select(_ => $"{_.Name} {_.Sensitive}"))}");
        }

        if (reader is IScryDisclosureEraser eraser)
        {
            await foreach (var erasure in eraser.Erasures())
            {
                told.AppendLine($"erased {erasure.Source}{erasure.Key} by {Named(erasure.By)}: {erasure.Units}");
            }
        }

        return told.ToString();
    }

    static string Named(string? caller) =>
        caller ?? "(nobody)";

    static string Ended(ScryDisclosureClose? close)
    {
        if (close is null)
        {
            return "never closed";
        }

        return $"{close.Outcome}, {close.Units}";
    }

    static string Receiver(ScryRowDisclosure row)
    {
        var to = Named(row.Event.Caller);
        if (row.Review is { } review)
        {
            to = $"{Named(review.Reviewer)} shown {to}'s";
        }

        return $"{to} #{row.Ordinal} via '{row.Via}' {row.Content}";
    }

    static string Sent(ScryDisclosedUnit unit)
    {
        if (unit.Erased)
        {
            return $"(erased, was {unit.Content.Length})";
        }

        if (!unit.Content.Held)
        {
            return $"(digest of {unit.Content.Length})";
        }

        return Encoding.UTF8.GetString(unit.Content.Bytes.Span);
    }

    public static async Task<long> Count(SqlDatabase<ClinicContext> database, string table) =>
        await Scalar<int>(database, $"SELECT COUNT(*) FROM [scry].[{table}]");

    public static async Task<T> Scalar<T>(SqlDatabase<ClinicContext> database, string query)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);
        return (T) (await command.ExecuteScalarAsync())!;
    }

    // Whether this server has ledger tables at all: SQL Server 2022 on. One that does not has no
    // ledger_type column in its catalog either, so a test that reads the column asks this first.
    public static async Task<bool> Ledgers(SqlDatabase<ClinicContext> database) =>
        await Scalar<int>(database, "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)") >= 16;

    public static async Task Execute(SqlDatabase<ClinicContext> database, string statement)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(statement, connection);
        await command.ExecuteNonQueryAsync();
    }
}

// Who the answers being recorded are for, changed between them.
sealed class Who
{
    public string Name { get; set; } = "dr.osei";
}

// A sink that hands every batch to two stores, so one sequence of answers is recorded to both.
sealed class Both(IScryDisclosureSink first, IScryDisclosureSink second) :
    IScryDisclosureSink
{
    public void Append(ScryDisclosureBatch batch)
    {
        first.Append(batch);
        second.Append(batch);
    }

    public async ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        await first.AppendAsync(batch, cancel);
        await second.AppendAsync(batch, cancel);
    }
}

// Somewhere other than the database to keep content: here, a dictionary. It counts what it is asked
// to keep, and can be told to refuse.
sealed class Blobs :
    IScryDisclosureBlobStore
{
    Dictionary<ScryDisclosureAddress, byte[]> kept = [];

    public volatile bool Refusing;

    int puts;

    public int Puts => puts;

    public int Count
    {
        get
        {
            lock (kept)
            {
                return kept.Count;
            }
        }
    }

    public ValueTask PutAsync(ScryDisclosureAddress address, ReadOnlyMemory<byte> bytes, Cancel cancel)
    {
        if (Refusing)
        {
            throw new IOException("The bucket is not there.");
        }

        Interlocked.Increment(ref puts);
        lock (kept)
        {
            kept[address] = bytes.ToArray();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(ScryDisclosureAddress address, Cancel cancel)
    {
        lock (kept)
        {
            if (kept.TryGetValue(address, out var bytes))
            {
                return new((ReadOnlyMemory<byte>?) bytes);
            }

            return new((ReadOnlyMemory<byte>?) null);
        }
    }

    public ValueTask DeleteAsync(ScryDisclosureAddress address, Cancel cancel)
    {
        lock (kept)
        {
            kept.Remove(address);
        }

        return ValueTask.CompletedTask;
    }
}
