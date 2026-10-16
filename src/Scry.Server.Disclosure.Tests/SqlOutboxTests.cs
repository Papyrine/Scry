/// <summary>
/// The SQL outbox: what "accepted" means for the SQL Server store. An answer is a committed row
/// before it is sent, the same batch is a row once however often it arrives, and a database that
/// cannot take the row means an answer that is not given.
/// </summary>
/// <remarks>
/// Every store here is told to leave what it accepts where it is: these tests read the outbox, and a
/// store left to itself empties it a moment after each answer.
/// </remarks>
public class SqlOutboxTests
{
    [Test]
    public async Task AnAcceptedBatchIsARowInTheOutbox()
    {
        await using var database = await Clinic.Instance.Build();
        var store = Store(database);
        var batch = Clinic.Batch(1);

        await store.AppendAsync(batch, Cancel.None);

        var row = (await Outbox(database)).Single();
        var kept = ScryDisclosureBatch.Deserialize(row.Batch);
        using (Assert.Multiple())
        {
            await Assert.That(row.EventId).IsEqualTo(batch.EventId);
            await Assert.That(row.Sequence).IsEqualTo(0);
            await Assert.That(kept.Begin!.Caller).IsEqualTo("dr.osei");
            await Assert.That(kept.Units.Single().Content).IsEqualTo(batch.Units[0].Content);
            await Assert.That(Encoding.UTF8.GetString(kept.Contents.Single().Bytes.Span)).IsEqualTo("{\"row\":1}");
            await Assert.That(kept.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Released);
        }
    }

    // A journal ships again after a crash it cannot tell from a success, and a retry after a timeout
    // does the same. The second arrival is not an error and not a second row.
    [Test]
    public async Task ABatchHandedOverTwiceIsKeptOnce()
    {
        await using var database = await Clinic.Instance.Build();
        var store = Store(database);
        var batch = Clinic.Batch(1);
        var next = new ScryDisclosureBatch
        {
            EventId = batch.EventId,
            Sequence = 1,
            Close = new(ScryDisclosureOutcome.Retracted, 0)
        };

        await store.AppendAsync(batch, Cancel.None);
        await store.AppendAsync(batch, Cancel.None);
        store.Append(batch);
        store.Append(next);

        await Assert.That((await Outbox(database)).Select(_ => _.Sequence)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ManyAtOnceAreAllKept()
    {
        await using var database = await Clinic.Instance.Build();
        var store = Store(database);
        var batches = Enumerable.Range(0, 64).Select(Clinic.Batch).ToList();

        await Task.WhenAll(batches.Select(_ => store.AppendAsync(_, Cancel.None).AsTask()));

        await Assert.That((await Outbox(database)).Select(_ => _.EventId)).IsEquivalentTo(batches.Select(_ => _.EventId));
    }

    // A deployment that creates its own objects turns the store's creating off, so the login a server
    // runs as needs no right to create anything, and runs the script itself.
    [Test]
    public async Task TheTablesAreTheDeploymentsToMakeWhereItSaysSo()
    {
        await using var database = await Clinic.Instance.Build();
        var store = Store(database, _ => _.CreateTables = false);

        await Assert.ThrowsExactlyAsync<SqlException>(async () => await store.AppendAsync(Clinic.Batch(1), Cancel.None));

        // Run twice, as a deployment that reruns its scripts would.
        await Execute(database, store.Script);
        await Execute(database, store.Script);
        await store.AppendAsync(Clinic.Batch(2), Cancel.None);

        await Assert.That(await Outbox(database)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task TheTablesGoWhereTheHostSays()
    {
        await using var database = await Clinic.Instance.Build();
        var store = Store(database, _ => _.Schema = "audit_2");

        await store.AppendAsync(Clinic.Batch(1), Cancel.None);

        await Assert.That(await Outbox(database, "audit_2")).Count().IsEqualTo(1);
    }

    // The schema is the one name written into the statements rather than passed to them, so it is held
    // to what a name may be before any statement is made.
    [Test]
    [Arguments("scry]; DROP TABLE Patients; --")]
    [Arguments("two words")]
    [Arguments("1st")]
    [Arguments("")]
    public async Task ASchemaIsHeldToAName(string schema)
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => new ScrySqlServerDisclosureStore(
                new()
                {
                    ConnectionString = "Server=(localdb)\\nowhere",
                    Schema = schema
                }));

        await Assert.That(exception.Message).Contains("must be a plain identifier");
    }

    // The store in the application's own database: the answer and the record of it wait on the same
    // server, so recording adds nothing that can be down.
    [Test]
    public async Task AnAnswerIsAcceptedIntoTheApplicationsOwnDatabase()
    {
        await using var database = await Clinic.Instance.Build();
        var processor = ScryProcessor.Create<ClinicContext>(
            _ => _.UseSqlServerDisclosureAudit(
                database.ConnectionString,
                _ => _.DrainInterval = null,
                _ => _.Caller = _ => "dr.osei"));

        var response = processor.Execute(Clinic.Names(), database.Context);

        var batch = ScryDisclosureBatch.Deserialize((await Outbox(database)).Single().Batch);
        using (Assert.Multiple())
        {
            await Assert.That(response.Payload.GetArrayLength()).IsEqualTo(3);
            await Assert.That(batch.Begin!.Kind).IsEqualTo(ScryDisclosureKind.List);
            await Assert.That(batch.Begin.Source).IsEqualTo("Patient");
            await Assert.That(batch.Begin.Caller).IsEqualTo("dr.osei");
            await Assert.That(batch.Contents.Where(_ => _.Kind == ScryDisclosureContentKind.Row).Select(_ => Encoding.UTF8.GetString(_.Bytes.Span)))
                .IsEquivalentTo(["{\"name\":\"Ada\"}", "{\"name\":\"Brook\"}", "{\"name\":\"Chidi\"}"], CollectionOrdering.Matching);
            await Assert.That(batch.Close!.Units).IsEqualTo(3);
        }
    }

    [Test]
    public async Task AnAnswerTheDatabaseCannotAcceptIsNotGiven()
    {
        await using var database = await Clinic.Instance.Build();
        var elsewhere = new SqlConnectionStringBuilder(database.ConnectionString)
        {
            InitialCatalog = "NoSuchDatabase",
            ConnectTimeout = 5
        };
        var processor = ScryProcessor.Create<ClinicContext>(
            _ => _.UseSqlServerDisclosureAudit(elsewhere.ConnectionString, configure: _ => _.Caller = _ => "dr.osei"));

        var exception = Assert.ThrowsExactly<ScryDisclosureException>(() => processor.Execute(Clinic.Names(), database.Context));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).StartsWith("The disclosure audit did not accept the record of this answer, so it was not sent:");
            await Assert.That(exception.InnerException).IsTypeOf<SqlException>();
        }
    }

    // The two durable accepts together: flushed to this machine's disk first, and a row in the
    // database a moment later, with the request waiting only on the first.
    [Test]
    public async Task AJournalShipsIntoTheOutbox()
    {
        await using var database = await Clinic.Instance.Build();
        var directory = Path.Combine(Path.GetTempPath(), "scry-journal-tests", Guid.NewGuid().ToString("N"));
        var batches = Enumerable.Range(0, 6).Select(Clinic.Batch).ToList();
        try
        {
            await using var journal = new ScryDisclosureJournal(directory, Store(database));
            foreach (var batch in batches)
            {
                await journal.AppendAsync(batch, Cancel.None);
            }

            await journal.DrainAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Assert.That((await Outbox(database)).Select(_ => _.EventId)).IsEquivalentTo(batches.Select(_ => _.EventId), CollectionOrdering.Matching);
    }

    static ScrySqlServerDisclosureStore Store(SqlDatabase<ClinicContext> database, Action<ScrySqlServerDisclosureOptions>? configure = null)
    {
        var options = new ScrySqlServerDisclosureOptions
        {
            ConnectionString = database.ConnectionString,
            DrainInterval = null
        };
        configure?.Invoke(options);
        return new(options);
    }

    static async Task<List<(long Position, Guid EventId, int Sequence, byte[] Batch)>> Outbox(SqlDatabase<ClinicContext> database, string schema = "scry")
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT [Position], [EventId], [Sequence], [Batch] FROM [{schema}].[DisclosureOutbox] ORDER BY [Position]", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(long, Guid, int, byte[])>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2), (byte[]) reader[3]));
        }

        return rows;
    }

    static async Task Execute(SqlDatabase<ClinicContext> database, string script)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(script, connection);
        await command.ExecuteNonQueryAsync();
    }
}
