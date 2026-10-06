using static SqlStores;

/// <summary>
/// The SQL Server store behind the outbox: what was accepted is moved into tables that are only ever
/// added to, once, and the three questions are answered from those tables as the in-memory store
/// answers them from what it was handed.
/// </summary>
public class SqlStoreTests
{
    // The same answers recorded to both stores, and every question asked of both. The in-memory store
    // is the plainest statement of what a store keeps, so agreeing with it is what correct means here.
    [Test]
    public async Task ItAnswersAsTheInMemoryStoreDoes()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var who = new Who();
        var processor = Processor(new Both(memory, kept), who);

        processor.Execute(Clinic.Names(), database.Context);
        who.Name = "nurse.kim";
        processor.Execute(WithDiagnosis(), database.Context);
        processor.Execute(Clinic.All("WardCensus", "Patients"), database.Context);
        processor.Execute(Clinic.All("Note", "Text"), database.Context);
        processor.Execute(QueryRequest.Create("Patient", [new CountOp()]), database.Context);
        processor.Execute(Admissions(), database.Context);
        await kept.DrainAsync();

        await Assert.That(await Told(kept)).IsEqualTo(await Told(memory));
        await Verify(await Told(kept));
    }

    // Asked for the row by the values of its key, in the key's own order: who was sent it, when, and
    // which version — here two callers, two different contents of one patient.
    [Test]
    public async Task WhoReceivedARow()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);
        var who = new Who();
        var processor = Processor(kept, who);

        processor.Execute(Clinic.Names(), database.Context);
        who.Name = "nurse.kim";
        processor.Execute(WithDiagnosis(), database.Context);
        await kept.DrainAsync();

        var received = await kept.ReceiversOf("Patient", [1]).ToListAsync();
        var morning = await kept.ReceiversOf("Patient", [1], to: received[1].Event.At).ToListAsync();
        var older = await kept.ReceiversOf("Patient", [1], after: ScryDisclosureCursor.After(received[0])).ToListAsync();
        using (Assert.Multiple())
        {
            await Assert.That(received.Select(_ => _.Event.Caller!)).IsEquivalentTo(["nurse.kim", "dr.osei"], CollectionOrdering.Matching);
            await Assert.That(received[0].Content).IsNotEqualTo(received[1].Content);
            await Assert.That(morning.Select(_ => _.Event.Caller!)).IsEquivalentTo(["dr.osei"], CollectionOrdering.Matching);
            await Assert.That(older.Select(_ => _.Event.Caller!)).IsEquivalentTo(["dr.osei"], CollectionOrdering.Matching);
            await Assert.That(await kept.ReceiversOf("Patient", [99]).ToListAsync()).IsEmpty();
            await Assert.That(await kept.MemberReceivedBy("nurse.kim", "Patient", "Diagnosis").ToListAsync()).Count().IsEqualTo(1);
            await Assert.That(await kept.MemberReceivedBy("dr.osei", "Patient", "Diagnosis").ToListAsync()).IsEmpty();
        }
    }

    // A ward reached through two of its patients left in two of the answer's rows. It is listed for
    // each, in the order they were sent, and a listing that stopped on the first takes up at the second.
    [Test]
    public async Task ARowCarriedByTwoUnitsIsListedForEach()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var processor = Processor(new Both(memory, kept), new());

        processor.Execute(WithDiagnosis(), database.Context);
        await kept.DrainAsync();

        foreach (var store in new IScryDisclosureReader[] {memory, kept})
        {
            var received = await store.ReceiversOf("Ward", [1]).ToListAsync();
            var rest = await store.ReceiversOf("Ward", [1], after: ScryDisclosureCursor.After(received[0])).ToListAsync();
            var none = await store.ReceiversOf("Ward", [1], after: ScryDisclosureCursor.After(received[1])).ToListAsync();
            using (Assert.Multiple())
            {
                await Assert.That(received.Select(_ => _.Ordinal)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
                await Assert.That(received.Select(_ => _.Via)).IsEquivalentTo(["Ward", "Ward"], CollectionOrdering.Matching);
                await Assert.That(rest.Select(_ => _.Ordinal)).IsEquivalentTo([1], CollectionOrdering.Matching);
                await Assert.That(none).IsEmpty();
            }
        }
    }

    // A journal ships again after a crash, a retry follows a timeout: the same batch arrives after it
    // was already moved on. It is recognised and dropped, and the record is what it was.
    [Test]
    public async Task ABatchIsMovedOnOnceHoweverOftenItArrives()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);
        var batch = Clinic.Batch(1);

        await kept.AppendAsync(batch, Cancel.None);
        var first = await kept.DrainAsync();
        await kept.AppendAsync(batch, Cancel.None);
        var again = await kept.DrainAsync();

        var status = await kept.Status();
        using (Assert.Multiple())
        {
            await Assert.That(first).IsEqualTo(1);
            await Assert.That(again).IsEqualTo(1);
            await Assert.That(status.Pending).IsEqualTo(0);
            await Assert.That(status.Events).IsEqualTo(1);
            await Assert.That((await kept.Reconstruct(batch.EventId))!.Units).Count().IsEqualTo(1);
            await Assert.That(await Count(database, "DisclosureUnit")).IsEqualTo(1);
        }
    }

    // What is sent again is the same content under the same address, so it is kept once: three answers
    // of three rows are three events, nine units, and four pieces of content — the rows and the request.
    [Test]
    public async Task ContentIsKeptOnceHoweverOftenItIsSent()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);
        var processor = Processor(kept, new());

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        processor.Execute(Clinic.Names(), database.Context);
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(await Count(database, "DisclosureEvent")).IsEqualTo(3);
            await Assert.That(await Count(database, "DisclosureUnit")).IsEqualTo(9);
            await Assert.That(await Count(database, "DisclosureEntity")).IsEqualTo(9);
            await Assert.That(await Count(database, "DisclosureContent")).IsEqualTo(4);
            await Assert.That(await Count(database, "DisclosureShape")).IsEqualTo(1);
            await Assert.That(await Count(database, "DisclosureField")).IsEqualTo(2);
        }
    }

    // Two nodes moving at once would each add the same rows. One holds the lock and moves; the other
    // finds it held and leaves it to the first.
    [Test]
    public async Task OnlyOneMovesAtATime()
    {
        await using var database = await Clinic.Instance.Build();
        await using var one = Store(database, _ => _.DrainBatches = 10);
        await using var two = Store(database, _ => _.DrainBatches = 10);
        var batches = Enumerable.Range(0, 120).Select(Clinic.Batch).ToList();
        foreach (var batch in batches)
        {
            await one.AppendAsync(batch, Cancel.None);
        }

        var moved = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(index => Task.Run(() => Mover(index).DrainAsync())));
        await one.DrainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(moved.Sum()).IsLessThanOrEqualTo(120);
            await Assert.That(await Count(database, "DisclosureEvent")).IsEqualTo(120);
            await Assert.That(await Count(database, "DisclosureUnit")).IsEqualTo(120);
            await Assert.That(await Count(database, "DisclosureOutbox")).IsEqualTo(0);
        }

        ScrySqlServerDisclosureStore Mover(int index)
        {
            if (index % 2 == 0)
            {
                return one;
            }

            return two;
        }
    }

    // Answers go on being accepted while earlier ones are moved on, by several nodes at once. Every
    // one of them ends up in the record once, none is lost on the way out of the outbox, and the chain
    // links them in one order however they interleaved.
    [Test]
    public async Task AnswersAcceptedWhileOthersAreMovedOnAreAllKept()
    {
        await using var database = await Clinic.Instance.Build();
        await using var one = Store(
            database,
            _ =>
            {
                _.DrainBatches = 7;
                _.HashChain = true;
            });
        await using var two = Store(
            database,
            _ =>
            {
                _.DrainBatches = 7;
                _.HashChain = true;
            });
        await one.EnsureCreatedAsync();
        var batches = Enumerable.Range(0, 240).Select(Clinic.Batch).ToList();
        var accepting = true;

        var accepted = Task.WhenAll(
            batches.Chunk(30).Select(
                (chunk, index) => Task.Run(
                    async () =>
                    {
                        foreach (var batch in chunk)
                        {
                            await Mover(index).AppendAsync(batch, Cancel.None);
                        }
                    })));
        var moving = Task.WhenAll(
            Enumerable.Range(0, 4).Select(
                index => Task.Run(
                    async () =>
                    {
                        while (Volatile.Read(ref accepting))
                        {
                            await Mover(index).DrainAsync();
                        }
                    })));
        await accepted;
        Volatile.Write(ref accepting, false);
        await moving;
        await one.DrainAsync();

        var check = await one.VerifyChainAsync();
        var all = await one.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        using (Assert.Multiple())
        {
            await Assert.That(all.Select(_ => _.Event.Id)).IsEquivalentTo(batches.Select(_ => _.EventId));
            await Assert.That(await Count(database, "DisclosureUnit")).IsEqualTo(240);
            await Assert.That(await Count(database, "DisclosureOutbox")).IsEqualTo(0);
            await Assert.That(check.Intact).IsTrue();
            await Assert.That(check.Records).IsEqualTo(240);
            await Assert.That(await Scalar<long>(database, "SELECT MAX([Link]) FROM [scry].[DisclosureChain]")).IsEqualTo(240);
        }

        ScrySqlServerDisclosureStore Mover(int index)
        {
            if (index % 2 == 0)
            {
                return one;
            }

            return two;
        }
    }

    // The tables the record is read from are only ever added to. Where the server has ledger tables
    // they are made as append-only ones, and the database itself refuses to change a row.
    [Test]
    public async Task WhereThereAreLedgerTablesTheRecordCannotBeRewritten()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);
        var batch = Clinic.Batch(1);
        await kept.AppendAsync(batch, Cancel.None);
        await kept.DrainAsync();

        if (!await Ledgers(database))
        {
            // No ledger tables on this server, and no column in its catalog to say which tables are.
            // The record was made in plain ones, and is read back from them like any other.
            await Assert.That((await kept.Reconstruct(batch.EventId))!.Units).Count().IsEqualTo(1);
            return;
        }

        var update = await Assert.ThrowsExactlyAsync<SqlException>(() => Execute(database, "UPDATE [scry].[DisclosureUnit] SET [Ordinal] = 7"));
        var delete = await Assert.ThrowsExactlyAsync<SqlException>(() => Execute(database, "DELETE FROM [scry].[DisclosureEvent]"));
        using (Assert.Multiple())
        {
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM sys.tables WHERE [name] LIKE N'Disclosure%' AND [ledger_type] = 3")).IsEqualTo(11);
            await Assert.That(update!.Message).Contains("append only Ledger table");
            await Assert.That(delete!.Message).Contains("append only Ledger table");
            await Assert.That((await kept.Reconstruct(batch.EventId))!.Units).Count().IsEqualTo(1);

            // What an erasure removes, and what leaves once it has been moved on, are not in them.
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM sys.tables WHERE [name] IN (N'DisclosureContent', N'DisclosureOutbox', N'DisclosureSource') AND [ledger_type] = 0")).IsEqualTo(3);
        }
    }

    // A host that wants plain tables gets them on any server, and they are then its own to change.
    [Test]
    public async Task TheHostSaysWhetherTheTablesAreLedgerTables()
    {
        await using var database = await Clinic.Instance.Build();
        await using var plain = Store(
            database,
            _ =>
            {
                _.Schema = "plain";
                _.LedgerTables = false;
            });

        await plain.AppendAsync(Clinic.Batch(1), Cancel.None);
        await plain.DrainAsync();
        await Execute(database, "UPDATE [plain].[DisclosureUnit] SET [Sequence] = [Sequence]");
        await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [plain].[DisclosureUnit]")).IsEqualTo(1);

        // Only a server that has ledger tables has the column that says which tables are.
        if (await Ledgers(database))
        {
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME([schema_id]) = N'plain' AND [ledger_type] <> 0")).IsEqualTo(0);
        }
    }

    // For a server without ledger tables: every batch is linked into a chain, and a check makes each
    // link's digest again from the rows as they now are. A row changed behind the record's back is the
    // link that no longer matches.
    [Test]
    public async Task TheChainShowsWhereTheRecordWasChanged()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(
            database,
            _ =>
            {
                _.HashChain = true;
                _.LedgerTables = false;
            });
        var processor = Processor(kept, new());

        processor.Execute(Clinic.Names(), database.Context);
        processor.Execute(WithDiagnosis(), database.Context);
        processor.Execute(Admissions(), database.Context);
        await kept.DrainAsync();

        var whole = await kept.VerifyChainAsync();
        var head = (await kept.Status()).ChainHead;
        var changed = (await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync())[1].Event.Id;
        await Execute(database, $"UPDATE [scry].[DisclosureEntity] SET [RowKey] = N'[2]' WHERE [EventId] = '{changed}' AND [Ordinal] = 0");
        var broken = await kept.VerifyChainAsync();
        var link = await Scalar<long>(database, $"SELECT [Link] FROM [scry].[DisclosureChain] WHERE [EventId] = '{changed}'");

        using (Assert.Multiple())
        {
            await Assert.That(whole.Intact).IsTrue();
            await Assert.That(whole.Records).IsEqualTo(3);
            await Assert.That(head).IsNotNull();
            await Assert.That(link).IsEqualTo(2);
            await Assert.That(broken.Intact).IsFalse();
            await Assert.That(broken.BrokenAt).IsEqualTo(link);
            await Assert.That((await kept.Status()).LastVerification).IsEqualTo(broken);

            // From the link after it, the chain still holds.
            await Assert.That((await kept.VerifyChainAsync(from: link + 1)).Intact).IsTrue();
        }
    }

    // A link taken out of the chain shows too: the one after it no longer follows from the one before.
    [Test]
    public async Task TheChainShowsWhereALinkWasRemoved()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(
            database,
            _ =>
            {
                _.HashChain = true;
                _.LedgerTables = false;
            });
        foreach (var index in Enumerable.Range(0, 5))
        {
            await kept.AppendAsync(Clinic.Batch(index), Cancel.None);
            await kept.DrainAsync();
        }

        await Execute(database, "DELETE FROM [scry].[DisclosureChain] WHERE [Link] = 3");
        var check = await kept.VerifyChainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(check.Intact).IsFalse();
            await Assert.That(check.BrokenAt).IsEqualTo(4);
        }
    }

    // A chain with its end cut off still holds, link by link. What shows it is the head as it was,
    // written down somewhere else: the chain no longer has it.
    [Test]
    public async Task AHeadKeptElsewhereShowsAChainCutShort()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(
            database,
            _ =>
            {
                _.HashChain = true;
                _.LedgerTables = false;
            });
        foreach (var index in Enumerable.Range(0, 5))
        {
            await kept.AppendAsync(Clinic.Batch(index), Cancel.None);
            await kept.DrainAsync();
        }

        var head = (await kept.Status()).ChainHead!.Value;
        var before = await kept.FindLinkAsync(head);
        await Execute(database, "DELETE FROM [scry].[DisclosureChain] WHERE [Link] > 3");
        var check = await kept.VerifyChainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(before).IsEqualTo(5);
            await Assert.That(check.Intact).IsTrue();
            await Assert.That(check.Records).IsEqualTo(3);
            await Assert.That(await kept.FindLinkAsync(head)).IsNull();
            await Assert.That(await kept.FindLinkAsync((await kept.Status()).ChainHead!.Value)).IsEqualTo(3);
        }
    }

    // Erasing a row removes what was sent of it and keeps that it was sent. An answer accepted a moment
    // ago, still in the outbox, is erased with the rest.
    [Test]
    public async Task ErasingARowRemovesItsContentAndKeepsTheRecord()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var who = new Who();
        var processor = Processor(new Both(memory, kept), who);

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        who.Name = "nurse.kim";
        processor.Execute(WithDiagnosis(), database.Context);

        var erasure = await kept.EraseAsync("Patient", [1], by: "records.officer");
        await memory.EraseAsync("Patient", [1], by: "records.officer");

        var first = (await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).Single().Event.Id;
        var second = (await kept.ReceivedBy("nurse.kim", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).Single().Event.Id;
        var units = (await kept.Reconstruct(first))!.Units;
        using (Assert.Multiple())
        {
            // Two pieces of content carried the patient: the name alone, and the row with the diagnosis.
            await Assert.That(erasure.Units).IsEqualTo(2);
            await Assert.That(erasure.Key).IsEqualTo("[1]");
            await Assert.That(units[0].Erased).IsTrue();
            await Assert.That(units[0].Content.Held).IsFalse();
            await Assert.That(units[0].Content.Length).IsEqualTo(14);
            await Assert.That(units[1].Erased).IsFalse();
            await Assert.That(Encoding.UTF8.GetString(units[1].Content.Bytes.Span)).IsEqualTo("{\"name\":\"Brook\"}");
            await Assert.That((await kept.Reconstruct(second))!.Units[0].Erased).IsTrue();
            await Assert.That((await kept.Status()).Pending).IsEqualTo(0);

            // That it was sent, to whom and when, is still there to be asked.
            await Assert.That(await kept.ReceiversOf("Patient", [1]).ToListAsync()).Count().IsEqualTo(2);
            await Assert.That((await kept.Erasures().ToListAsync()).Single()).IsEqualTo(erasure);
            await Assert.That(await Told(kept)).IsEqualTo(await Told(memory));
        }

        // Erased again, there is nothing left to remove, and that it was asked is recorded all the same.
        var again = await kept.EraseAsync("Patient", [1], by: "records.officer");
        await memory.EraseAsync("Patient", [1], by: "records.officer");

        // Sent again, it is a new disclosure and is kept as one.
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        using (Assert.Multiple())
        {
            await Assert.That(again.Units).IsEqualTo(0);
            await Assert.That((await kept.Reconstruct(first))!.Units[0].Content.Held).IsTrue();
            await Assert.That((await kept.Reconstruct(second))!.Units[0].Erased).IsTrue();
            await Assert.That(await Told(kept)).IsEqualTo(await Told(memory));
        }

        await Verify(await Told(kept));
    }

    // Content kept outside the database: the table says only that it is elsewhere, an event is rebuilt
    // by asking for it back, and an erasure removes it from there before it says so here.
    [Test]
    public async Task ContentCanBeKeptOutsideTheDatabase()
    {
        await using var database = await Clinic.Instance.Build();
        var blobs = new Blobs();
        await using var kept = Store(database, _ => _.Content = blobs);
        var processor = Processor(kept, new());

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();

        // Sent again, it is already kept there, and is not handed over a second time.
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        var puts = blobs.Puts;

        var id = (await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync())[1].Event.Id;
        var rebuilt = (await kept.Reconstruct(id))!;
        await kept.EraseAsync("Patient", [2], by: null);

        using (Assert.Multiple())
        {
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry].[DisclosureContent] WHERE [Bytes] IS NOT NULL")).IsEqualTo(0);
            await Assert.That(puts).IsEqualTo(4);
            await Assert.That(rebuilt.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span))).IsEquivalentTo(
                ["{\"name\":\"Ada\"}", "{\"name\":\"Brook\"}", "{\"name\":\"Chidi\"}"],
                CollectionOrdering.Matching);
            await Assert.That(Encoding.UTF8.GetString(rebuilt.Request!.Value.Span)).Contains("\"Patient\"");
            await Assert.That(blobs.Count).IsEqualTo(3);
            await Assert.That((await kept.Reconstruct(id))!.Units[1].Erased).IsTrue();
            await Assert.That((await kept.Reconstruct(id))!.Units[1].Content.Held).IsFalse();
        }

        // And sent after it was erased, it is kept there again.
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        using (Assert.Multiple())
        {
            await Assert.That(blobs.Puts).IsEqualTo(5);
            await Assert.That(blobs.Count).IsEqualTo(4);
            await Assert.That((await kept.Reconstruct(id))!.Units[1].Content.Held).IsTrue();
        }
    }

    // A batch that will not read back is set aside where it lies, and said so, rather than left at the
    // head of the outbox with everything accepted after it waiting behind.
    [Test]
    public async Task ABatchThatCannotBeReadDoesNotHoldUpTheRest()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);
        await kept.EnsureCreatedAsync();
        await Execute(database, "INSERT INTO [scry].[DisclosureOutbox] ([EventId], [Sequence], [Batch]) VALUES (NEWID(), 0, 0x0102030405)");
        var batch = Clinic.Batch(1);
        await kept.AppendAsync(batch, Cancel.None);

        await kept.DrainAsync();
        await kept.DrainAsync();

        var status = await kept.Status();
        using (Assert.Multiple())
        {
            await Assert.That(await kept.Reconstruct(batch.EventId)).IsNotNull();
            await Assert.That(status.Pending).IsEqualTo(1);
            await Assert.That(status.Problem).Contains("could not be read back");
            await Assert.That(await Count(database, "DisclosureBatch")).IsEqualTo(1);
        }
    }

    // Left to itself the store moves on what it accepts, at once, and what other nodes accepted at the
    // next interval.
    [Test]
    public async Task LeftToItselfItMovesOnWhatItAccepts()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database, _ => _.DrainInterval = TimeSpan.FromMilliseconds(100));
        await using var other = Store(database);
        var mine = Clinic.Batch(1);
        var theirs = Clinic.Batch(2);

        await kept.AppendAsync(mine, Cancel.None);
        await other.AppendAsync(theirs, Cancel.None);
        await Emptied(kept);

        using (Assert.Multiple())
        {
            await Assert.That(await kept.Reconstruct(mine.EventId)).IsNotNull();
            await Assert.That(await kept.Reconstruct(theirs.EventId)).IsNotNull();
        }
    }

    // A move that fails loses nothing: what it could not move is still in the outbox, the store says
    // what is wrong, and it is tried again until it goes.
    [Test]
    public async Task AMoveThatFailsIsSaidAndTriedAgain()
    {
        await using var database = await Clinic.Instance.Build();
        var blobs = new Blobs
        {
            Refusing = true
        };
        await using var kept = Store(
            database,
            _ =>
            {
                _.Content = blobs;
                _.DrainInterval = TimeSpan.FromMilliseconds(100);
            });
        var batch = Clinic.Batch(1);

        await kept.AppendAsync(batch, Cancel.None);
        await Until(async () => (await kept.Status()).Problem is not null, "The store did not say that it could not move a batch on.");
        var stuck = await kept.Status();
        var before = await kept.Reconstruct(batch.EventId);
        blobs.Refusing = false;
        await Emptied(kept);
        await Until(async () => (await kept.Status()).Problem is null, "The store went on saying something was wrong after it was not.");

        using (Assert.Multiple())
        {
            await Assert.That(stuck.Pending).IsEqualTo(1);
            await Assert.That(stuck.Problem).Contains("The bucket is not there.");
            await Assert.That(before).IsNull();
            await Assert.That((await kept.Reconstruct(batch.EventId))!.Units.Single().Content.Held).IsTrue();
        }
    }

    // Through the container, the one store is the sink, the reader, the eraser and the status, and the
    // host starts it moving before it has accepted anything — which is what moves on what this node
    // left behind when it last stopped, and what other nodes accept.
    [Test]
    public async Task TheContainerHandsOutOneStoreAndTheHostStartsItMoving()
    {
        await using var database = await Clinic.Instance.Build();
        await using var other = Store(database);
        var services = new ServiceCollection();
        services.AddScry<ClinicContext>(
            _ => _.UseSqlServerDisclosureAudit(
                database.ConnectionString,
                _ => _.DrainInterval = TimeSpan.FromMilliseconds(100),
                _ => _.Caller = _ => "dr.osei"));

        // Closed as a container that knows nothing of asynchronous disposal would close it, which the
        // store has to put up with: it is handed out by a factory, so the container closes it.
        using var provider = services.BuildServiceProvider();
        var kept = provider.GetRequiredService<ScrySqlServerDisclosureStore>();
        var hosted = provider.GetServices<IHostedService>().ToList();
        var left = Clinic.Batch(1);
        await other.AppendAsync(left, Cancel.None);

        foreach (var service in hosted)
        {
            await service.StartAsync(Cancel.None);
        }

        try
        {
            await Emptied(kept);
            using (Assert.Multiple())
            {
                await Assert.That(provider.GetRequiredService<IScryDisclosureSink>()).IsSameReferenceAs(kept);
                await Assert.That(provider.GetRequiredService<IScryDisclosureReader>()).IsSameReferenceAs(kept);
                await Assert.That(provider.GetRequiredService<IScryDisclosureEraser>()).IsSameReferenceAs(kept);
                await Assert.That(provider.GetRequiredService<IScryDisclosureStatus>()).IsSameReferenceAs(kept);
                await Assert.That(await kept.Reconstruct(left.EventId)).IsNotNull();
            }
        }
        finally
        {
            foreach (var service in hosted)
            {
                await service.StopAsync(Cancel.None);
            }
        }
    }

    // A question can arrive before the first answer has. The record is then asked of a database that
    // has none of its tables yet — which is an empty record, and not a failure.
    [Test]
    public async Task ARecordNothingHasBeenWrittenToIsEmpty()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);

        using (Assert.Multiple())
        {
            await Assert.That((await kept.Catalog()).Sources).IsEmpty();
            await Assert.That(await kept.ReceiversOf("Patient", [1]).ToListAsync()).IsEmpty();
            await Assert.That(await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).IsEmpty();
            await Assert.That(await kept.MemberReceivedBy("dr.osei", "Patient", "Name").ToListAsync()).IsEmpty();
            await Assert.That(await kept.Reconstruct(Guid.NewGuid())).IsNull();
            await Assert.That(await kept.Reviews().ToListAsync()).IsEmpty();
            await Assert.That(await kept.Erasures().ToListAsync()).IsEmpty();
            await Assert.That(await kept.FindLinkAsync(default)).IsNull();
            await Assert.That((await kept.Status()).Events).IsEqualTo(0);
        }
    }

    // A listing is read a page at a time and taken up where the last page ended, so a caller with more
    // events than a page holds is still answered whole, newest first.
    [Test]
    public async Task ALongListingIsReadToItsEnd()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);
        var batches = Enumerable.Range(0, 450).Select(Clinic.Batch).ToList();
        await Task.WhenAll(batches.Select(_ => kept.AppendAsync(_, Cancel.None).AsTask()));
        await kept.DrainAsync();

        var all = await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        var tail = await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, after: ScryDisclosureCursor.After(all[299])).ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(all).Count().IsEqualTo(450);
            await Assert.That(all.Select(_ => _.Event.Id).Distinct()).Count().IsEqualTo(450);
            await Assert.That(all.Select(_ => _.Event.At)).IsInDescendingOrder();
            await Assert.That(tail.Select(_ => _.Event.Id)).IsEquivalentTo(all.Skip(300).Select(_ => _.Event.Id), CollectionOrdering.Matching);
        }
    }

    // Reading the record is itself recorded, through the same sink, and a reviewer shown an event is
    // from then on among those who received its rows.
    [Test]
    public async Task AReviewerShownAnEventReceivedItsRows()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var both = new Both(memory, kept);
        var processor = Processor(both, new());
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        var shown = (await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).Single().Event.Id;
        var review = new ScryDisclosureReview(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(1), ScryDisclosureQuestion.Event)
        {
            Reviewer = "records.officer",
            Results = 1,
            Events = [shown]
        };

        await both.AppendAsync(
            new()
            {
                EventId = review.Id,
                Review = review
            },
            Cancel.None);
        await kept.DrainAsync();

        var received = await kept.ReceiversOf("Patient", [1]).ToListAsync();
        var reviews = await kept.Reviews().ToListAsync();
        using (Assert.Multiple())
        {
            await Assert.That(received).Count().IsEqualTo(2);
            await Assert.That(received[0].Review!.Reviewer).IsEqualTo("records.officer");
            await Assert.That(received[0].Event.Id).IsEqualTo(shown);
            await Assert.That(received[1].Review).IsNull();
            await Assert.That(reviews.Single().Events).IsEquivalentTo([shown]);
            await Assert.That(reviews.Single().Reviewer).IsEqualTo("records.officer");
            await Assert.That(reviews.Single().At).IsEqualTo(review.At);
            await Assert.That((await kept.Status()).Events).IsEqualTo(1);
            await Assert.That(await Told(kept)).IsEqualTo(await Told(memory));
        }
    }

    static QueryRequest WithDiagnosis() =>
        QueryRequest.Create(
            "Patient",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Ward", "Name"]),
                        new ConstNode("North", ClrTypeTag.String))),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(
                    new(
                    [
                        new("Name", new NodeValue(new MemberNode(["Name"]))),
                        new("Diagnosis", new NodeValue(new MemberNode(["Diagnosis"]))),
                        new("Ward", new NodeValue(new MemberNode(["Ward", "Name"])))
                    ]))
            ]);

    static QueryRequest Admissions() =>
        QueryRequest.Create(
            "Admission",
            [
                new OrderByOp(new MemberNode(["Reason"]), Descending: false),
                new ThenByOp(new MemberNode(["PatientId"]), Descending: false),
                new SelectOp(new([new("Reason", new NodeValue(new MemberNode(["Reason"])))]))
            ]);
}
