using Microsoft.EntityFrameworkCore;

// RenameShift is deprecated in the test model, to exercise a deprecated command; sending it still
// has to name it.
#pragma warning disable CS0618

/// <summary>
/// Every other way a server hands something to a caller, each recorded before it is handed over: a
/// stream a chunk at a time, a live query's answers and only the ones that were sent, an attachment,
/// a command's receipts, a refusal that says a row exists, the schema, the SQL a query would run.
/// </summary>
[NotInParallel]
public class DisclosureSurfaceTests
{
    static TimeSpan patience = TimeSpan.FromSeconds(30);

    // Sixteen bytes a row and twenty to a chunk: two rows fill one. The record goes to the sink a
    // chunk at a time, each before any row of it is handed on, and the lines are the lines a stream
    // always wrote.
    [Test]
    public async Task AStreamIsRecordedAChunkAtATime()
    {
        var store = new ScryMemoryDisclosureStore();
        var accepted = 0;
        var tap = new TappedSink(store)
        {
            Accepting = _ => accepted += _.Units.Count
        };
        var processor = Disclosures.Audited(tap, configure: _ => _.StreamChunkBytes = 20);
        List<string> ahead = [];

        await using var context = TestContext.CreateSeeded();
        var plain = await Lines(SharedProcessor.Instance, context);
        var (_, _, rows) = processor.StreamBuffered(Disclosures.Names(), context, EmptyServiceProvider.Instance, new HeaderDictionary(), new HeaderDictionary());
        List<string> lines = [];
        await foreach (var row in rows)
        {
            lines.Add(Encoding.UTF8.GetString(row.Span));
            if (lines.Count > accepted)
            {
                ahead.Add($"line {lines.Count} was handed on with {accepted} on record");
            }
        }

        var recorded = (await Disclosures.Events(store)).Single();
        using (Assert.Multiple())
        {
            await Assert.That(lines).IsEquivalentTo(plain, CollectionOrdering.Matching);
            await Assert.That(ahead).IsEmpty();
            await Assert.That(tap.Batches.Select(_ => _.Units.Count)).IsEquivalentTo([2, 1, 0], CollectionOrdering.Matching);
            await Assert.That(recorded.Event.Kind).IsEqualTo(ScryDisclosureKind.Stream);
            await Assert.That(recorded.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Released);
            await Assert.That(recorded.Close.Units).IsEqualTo(3);
        }

        await Verify(await Disclosures.Shape(store));
    }

    // A reader that stops is recorded as having been handed what it was handed and no more: the chunk
    // it was part-way through had been accepted whole, and the close says how far into it they got.
    [Test]
    public async Task AStreamTheReaderAbandonsCountsOnlyWhatWasHandedOn()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.StreamChunkBytes = 20);
        var auditor = new RecordingAuditor();
        await using var services = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        var (_, rows) = processor.Stream(Disclosures.Names(), context, services);
        await foreach (var _ in rows)
        {
            break;
        }

        var recorded = (await Disclosures.Events(store)).Single();
        var answer = (await store.Reconstruct(recorded.Event.Id))!;
        var second = JsonSerializer.Deserialize<int[]>(answer.Units[1].Entities.Single().Key)!.Cast<object?>().ToList();
        using (Assert.Multiple())
        {
            await Assert.That(recorded.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Canceled);
            await Assert.That(recorded.Close.Units).IsEqualTo(1);

            // The second row was on record and never handed on, so nobody is said to have received it.
            await Assert.That(answer.Units).Count().IsEqualTo(2);
            await Assert.That(await store.ReceiversOf("Employee", second).ToListAsync()).IsEmpty();
            await Assert.That(auditor.Entries.Single().Outcome).IsEqualTo(ScryQueryOutcome.Canceled);
            await Assert.That(auditor.Entries.Single().Rows).IsEqualTo(1);
            await Assert.That(auditor.Entries.Single().Disclosure).IsEqualTo(recorded.Event.Id);
        }
    }

    // A stream cut short by a limit ends as it always did: the rows read before the limit, then the
    // refusal. With the audit on those rows are recorded first and are the same rows.
    [Test]
    [Arguments("rows")]
    [Arguments("bytes")]
    public async Task AStreamALimitCutsShortHandsOnTheSameRowsFirst(string limit)
    {
        void Limit(ScryOptions options)
        {
            if (limit == "rows")
            {
                options.MaxStreamRows = 2;
            }
            else
            {
                // Two lines of sixteen bytes and their newlines, and not a third.
                options.MaxResponseBytes = 40;
            }
        }

        var (processor, store) = Disclosures.Audited(Limit, _ => _.StreamChunkBytes = 1024);
        var plain = ScryProcessor.Create<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                Limit(options);
            });

        var (before, refused) = await Cut(plain);
        var (lines, exception) = await Cut(processor);

        var recorded = (await Disclosures.Events(store)).Single();
        using (Assert.Multiple())
        {
            await Assert.That(lines).IsEquivalentTo(before, CollectionOrdering.Matching);
            await Assert.That(lines).Count().IsEqualTo(2);
            await Assert.That(exception.Message).IsEqualTo(refused.Message);
            await Assert.That(recorded.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Truncated);
            await Assert.That(recorded.Close.Units).IsEqualTo(2);
        }

        async Task<(List<string> Lines, ScryValidationException Refused)> Cut(ScryProcessor asked)
        {
            await using var context = TestContext.CreateSeeded();
            var options = new ScryOptions(typeof(TestContext));
            Limit(options);
            var (_, _, rows) = asked.StreamBuffered(
                Disclosures.Names(),
                context,
                EmptyServiceProvider.Instance,
                new HeaderDictionary(),
                new HeaderDictionary(),
                budget: ResponseBudget.For(options));
            List<string> read = [];
            var thrown = await Assert.ThrowsExactlyAsync<ScryValidationException>(
                async () =>
                {
                    await foreach (var row in rows)
                    {
                        read.Add(Encoding.UTF8.GetString(row.Span));
                    }
                });
            return (read, thrown!);
        }
    }

    // The sink goes down between chunks. The rows whose record it took are out; the rows it would
    // not take the record of are not, and the stream ends as the failure it is.
    [Test]
    public async Task AStreamStopsWhereTheRecordDoes()
    {
        var store = new ScryMemoryDisclosureStore();
        var processor = Disclosures.Audited(new RefusingSink(accepting: 1, inner: store), configure: _ => _.StreamChunkBytes = 20);
        var auditor = new RecordingAuditor();
        await using var services = Services(auditor);

        await using var context = TestContext.CreateSeeded();
        var (_, _, rows) = processor.StreamBuffered(Disclosures.Names(), context, services, new HeaderDictionary(), new HeaderDictionary());
        List<string> lines = [];
        await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            async () =>
            {
                await foreach (var row in rows)
                {
                    lines.Add(Encoding.UTF8.GetString(row.Span));
                }
            });

        using (Assert.Multiple())
        {
            await Assert.That(lines).IsEquivalentTo(["{\"name\":\"Aaron\"}", "{\"name\":\"Alice\"}"], CollectionOrdering.Matching);
            await Assert.That(auditor.Entries.Single().Outcome).IsEqualTo(ScryQueryOutcome.Failed);
        }
    }

    // A row with a binary value is recorded by the value's digest and sent with the value as a part.
    // It is written when it is handed on, one row at a time, so each row's part is collected just
    // before its line and every line's first part is numbered zero — as without the audit.
    [Test]
    public async Task AStreamOfBinaryRowsStillSendsEachRowsPartsWithItsLine()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.StreamChunkBytes = 4096);
        var request = Disclosures.From<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                _.Avatar
            })
            .ToScryRequest();

        var plain = await Parts(SharedProcessor.Instance);
        var audited = await Parts(processor);

        var units = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!.Units;
        using (Assert.Multiple())
        {
            await Assert.That(audited).IsEquivalentTo(plain, CollectionOrdering.Matching);
            await Assert.That(audited[0]).IsEqualTo("{\"name\":\"Aaron\",\"avatar\":{\"$bin\":0}} + 0A0B");
            await Assert.That(Encoding.UTF8.GetString(units[0].Content.Bytes.Span)).Contains("\"$bytes\"");
            await Assert.That(units).Count().IsEqualTo(plain.Count);
        }

        async Task<List<string>> Parts(ScryProcessor asked)
        {
            await using var context = TestContext.CreateSeeded();
            var collector = new BinaryPartCollector();
            var (_, diverting, rows) = asked.StreamBuffered(
                request,
                context,
                EmptyServiceProvider.Instance,
                new HeaderDictionary(),
                new HeaderDictionary(),
                binary: collector);
            await Assert.That(diverting).IsTrue();
            List<string> read = [];
            await foreach (var row in rows)
            {
                // As the endpoint does: this row's parts, then its line.
                var parts = string.Join(",", collector.Drain().Select(Convert.ToHexString));
                read.Add($"{Encoding.UTF8.GetString(row.Span)} + {parts}");
            }

            return read;
        }
    }

    // A live query records an answer when it sends one, and only then. A run that found nothing new
    // for this caller sent nothing, so there is nothing of it in the record — though it ran, and is
    // audited as having run.
    [Test]
    public async Task ALiveQueryRecordsTheAnswersItSends()
    {
        await using var database = await Seeded("DisclosureLive");
        var store = new ScryMemoryDisclosureStore();
        var processor = Live(store);
        var runs = new RunCounter();
        await using var services = new ServiceCollection().AddSingleton<IScryAuditor>(runs).BuildServiceProvider();
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions("North"), reading, services)
            .GetAsyncEnumerator();
        await Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience)).IsTrue();

        // A write this query's answer does not show: it runs, and says nothing.
        var pending = answers.MoveNextAsync().AsTask();
        await Insert(database, processor, "West");
        await runs.Reaches(2);
        var quiet = await Disclosures.Events(store);

        // And one it does.
        await Insert(database, processor, "North");
        await Assert.That(await pending.WaitAsync(patience)).IsTrue();
        await runs.Reaches(3);

        var events = await Disclosures.Events(store);
        var entries = runs.Entries;
        using (Assert.Multiple())
        {
            await Assert.That(quiet).Count().IsEqualTo(1);
            await Assert.That(events).Count().IsEqualTo(2);
            await Assert.That(events.All(_ => _.Event.Subscribed)).IsTrue();
            await Assert.That(events.All(_ => _.Event.Delivery == ScryDisclosureDelivery.Sent)).IsTrue();
            await Assert.That(events[0].Close!.Units).IsEqualTo(1);
            await Assert.That(events[1].Close!.Units).IsEqualTo(2);

            // One live query, so its answers are tied together, each by which run it was.
            await Assert.That(events[0].Event.Correlation).EndsWith("/0");
            await Assert.That(events[1].Event.Correlation).EndsWith("/2");
            await Assert.That(events[0].Event.Correlation![..^2]).IsEqualTo(events[1].Event.Correlation![..^2]);

            // Every run is audited; only the ones that sent something name an event.
            await Assert.That(entries.Select(_ => _.Disclosure)).IsEquivalentTo(
                new Guid?[] {events[0].Event.Id, null, events[1].Event.Id},
                CollectionOrdering.Matching);
        }
    }

    // A client asking again names the answer it already holds. Where the first answer is that one,
    // it is told so rather than sent it — and the record says it was confirmed, not sent.
    [Test]
    public async Task AnAnswerTheCallerAlreadyHoldsIsRecordedAsConfirmed()
    {
        await using var database = await Seeded("DisclosureResume");
        var store = new ScryMemoryDisclosureStore();
        var processor = Live(store);
        await using var reading = database.NewDbContext();

        var held = await First(resumeFrom: null);
        await First(resumeFrom: held);
        await First(resumeFrom: "something else");

        await Assert.That((await Disclosures.Events(store)).Select(_ => _.Event.Delivery)).IsEquivalentTo(
            [ScryDisclosureDelivery.Sent, ScryDisclosureDelivery.Confirmed, ScryDisclosureDelivery.Sent],
            CollectionOrdering.Matching);

        async Task<string> First(string? resumeFrom)
        {
            using var ending = new CancelSource();
            await using var answers = processor
                .SubscribeBuffered(
                    Regions("North"),
                    reading,
                    EmptyServiceProvider.Instance,
                    new HeaderDictionary(),
                    new HeaderDictionary(),
                    caller: null,
                    ending.Token,
                    resumeFrom)
                .GetAsyncEnumerator(ending.Token);
            await Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience)).IsTrue();
            return answers.Current.Id;
        }
    }

    [Test]
    public async Task ALiveAnswerTheSinkRefusesIsNotGiven()
    {
        await using var database = await Seeded("DisclosureLiveRefused");
        var processor = Live(new RefusingSink());
        var runs = new RunCounter();
        await using var services = new ServiceCollection().AddSingleton<IScryAuditor>(runs).BuildServiceProvider();
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions("North"), reading, services)
            .GetAsyncEnumerator();

        await Assert.ThrowsExactlyAsync<ScryDisclosureException>(async () => await answers.MoveNextAsync().AsTask().WaitAsync(patience));

        await Assert.That(runs.Entries.Single().Outcome).IsEqualTo(ScryQueryOutcome.Failed);
    }

    // An attachment is recorded as the row it belongs to, the member, what kind of bytes they were,
    // and their digest — the same digest the same bytes would have in a row. A row with no document is
    // recorded as that; a fetch that found nothing sent nothing.
    [Test]
    public async Task AnAttachmentIsRecordedByItsRowAndItsDigest()
    {
        var (processor, store) = Disclosures.Audited();
        await using var context = TestContext.CreateSeeded();

        var found = processor.FetchAttachment(Document(1), context);
        var empty = processor.FetchAttachment(Document(2), context);
        var refused = processor.FetchAttachment(Document(UnsealedContractsPolicy.SealedId), context);
        var missing = processor.FetchAttachment(Document(99), context);

        var events = await Disclosures.Events(store);
        byte[] tagged = [(byte) ScryDisclosureContentKind.Bytes, 0x11, 0x22, 0x33];
        using (Assert.Multiple())
        {
            await Assert.That(found.Value).IsNotNull();
            await Assert.That(empty.Found).IsTrue();
            await Assert.That(refused.Found).IsFalse();
            await Assert.That(missing.Found).IsFalse();
            await Assert.That(events).Count().IsEqualTo(2);
            await Assert.That(events[0].Event.Kind).IsEqualTo(ScryDisclosureKind.Attachment);
            await Assert.That(events[0].Event.ContentType).IsEqualTo("application/pdf");

            var document = (await store.Reconstruct(events[0].Event.Id))!;
            await Assert.That(document.Units.Single().Content.Address).IsEqualTo(ScryDisclosureAddress.From(SHA256.HashData(tagged)));
            await Assert.That(document.Units.Single().Content.Length).IsEqualTo(3);
            await Assert.That(document.Units.Single().Content.Held).IsFalse();
            await Assert.That(Disclosures.Row(document.Units.Single().Entities.Single())).IsEqualTo("Contract[1]");
            await Assert.That(document.Shape!.Fields.Select(Disclosures.Field)).IsEquivalentTo(["Contract.Document: Returned"]);
            await Assert.That(Encoding.UTF8.GetString(document.Request!.Value.Span)).Contains("\"Document\"");

            var nothing = (await store.Reconstruct(events[1].Event.Id))!;
            await Assert.That(Encoding.UTF8.GetString(nothing.Units.Single().Content.Bytes.Span)).IsEqualTo("null");
            await Assert.That(Disclosures.Row(nothing.Units.Single().Entities.Single())).IsEqualTo("Contract[2]");
            await Assert.That(await store.MemberReceivedBy(Disclosures.Caller, "Contract", "Document").ToListAsync()).Count().IsEqualTo(2);
        }
    }

    // A command's receipt is what its sender is told, each time they are told: how it stood, what it
    // answered with, and the row it was sent against.
    [Test]
    public async Task ACommandsReceiptsAreRecordedEachTimeTheyAreGiven()
    {
        var store = new ScryMemoryDisclosureStore();
        await using var host = await CommandHost.Start("DisclosureCommands", _ => _.UseDisclosureAudit(store));
        var id = Guid.NewGuid();

        var sent = await host.Send("RenameShift", new {id = 1, name = "Night"}, caller: "alice", id: id);
        List<CommandReceipt> again = [];
        await foreach (var receipt in host.Processor.Receipt(id, "alice"))
        {
            again.Add(receipt);
        }

        await using var reading = host.Database.NewDbContext();
        var may = host.Processor.Capabilities(reading, host.Services, new HeaderDictionary(), "alice");

        var events = await Disclosures.Events(store, "alice");
        using (Assert.Multiple())
        {
            await Assert.That(sent.Single().Status).IsEqualTo(CommandStatus.Completed);
            await Assert.That(again.Single().Status).IsEqualTo(CommandStatus.Completed);
            await Assert.That(may.Commands).IsNotEmpty();
            await Assert.That(events.Select(_ => _.Event.Kind)).IsEquivalentTo(
                [ScryDisclosureKind.CommandReceipt, ScryDisclosureKind.CommandReceipt, ScryDisclosureKind.Capabilities],
                CollectionOrdering.Matching);

            var first = (await store.Reconstruct(events[0].Event.Id))!;
            await Assert.That(first.Event.Source).IsEqualTo("Shift");
            await Assert.That(Disclosures.Row(first.Units.Single().Entities.Single())).IsEqualTo("Shift[1] via target");
            await Assert.That(first.Units.Single().Content.Kind).IsEqualTo(ScryDisclosureContentKind.Receipt);
            await Assert.That(Encoding.UTF8.GetString(first.Units.Single().Content.Bytes.Span)).Contains("\"Completed\"");
            await Assert.That(Encoding.UTF8.GetString(first.Request!.Value.Span)).Contains("\"RenameShift\"");

            // Told twice, the same receipt is one content and two events.
            var second = (await store.Reconstruct(events[1].Event.Id))!;
            await Assert.That(second.Units.Single().Content.Address).IsEqualTo(first.Units.Single().Content.Address);

            var allowed = (await store.Reconstruct(events[2].Event.Id))!;
            await Assert.That(allowed.Units.Single().Content.Kind).IsEqualTo(ScryDisclosureContentKind.Capabilities);
        }
    }

    // A policy set to refuse rather than hide tells the caller that a row it may not read matched.
    // Nothing was sent, and that much was learned: it is recorded as a denial, with what was asked.
    [Test]
    public async Task ARefusalThatSaysARowExistsIsRecorded()
    {
        var (processor, store) = Disclosures.Audited(
            _ => _.AddPolicy<Employee, ActiveOnlyPolicy>(
                new()
                {
                    RootList = DeniedRowMode.Error
                }));
        var auditor = new RecordingAuditor();
        await using var services = Services(auditor);
        var everyone = Disclosures.From<Employee>("Employee")
            .Select(_ => new
            {
                _.Name
            })
            .ToScryRequest();

        await Assert.ThrowsExactlyAsync<ScryPermissionException>(() => Disclosures.Buffered(processor, everyone, services));
        Assert.ThrowsExactly<ScryPermissionException>(() => Disclosures.Direct(processor, everyone, services));

        var events = await Disclosures.Events(store);
        var denial = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(events.Select(_ => _.Event.Kind)).IsEquivalentTo(
                [ScryDisclosureKind.Denial, ScryDisclosureKind.Denial],
                CollectionOrdering.Matching);
            await Assert.That(denial.Units).IsEmpty();
            await Assert.That(Encoding.UTF8.GetString(denial.Request!.Value.Span)).Contains("\"Employee\"");
            await Assert.That(auditor.Entries.Select(_ => _.Outcome)).IsEquivalentTo(
                [ScryQueryOutcome.Denied, ScryQueryOutcome.Denied],
                CollectionOrdering.Matching);
            await Assert.That(auditor.Entries[0].Disclosure).IsEqualTo(events[0].Event.Id);
        }
    }

    // The schema is the map of everything there is to ask for, so who was handed it is recorded. It
    // is one document however often it is read, and a host reading its own surface records nothing.
    [Test]
    public async Task ReadingTheSchemaIsRecordedAndTheSchemaIsKeptOnce()
    {
        var (processor, store) = Disclosures.Audited();

        processor.Describe();
        var before = store.ContentCount;
        var described = processor.Describe(EmptyServiceProvider.Instance);
        processor.Describe(EmptyServiceProvider.Instance, "bob");

        var mine = (await Disclosures.Events(store)).Single();
        var schema = (await store.Reconstruct(mine.Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(before).IsEqualTo(0);
            await Assert.That(store.ContentCount).IsEqualTo(1);
            await Assert.That(mine.Event.Kind).IsEqualTo(ScryDisclosureKind.Schema);
            await Assert.That(mine.Event.Source).IsEqualTo("");
            await Assert.That(schema.Units.Single().Content.Kind).IsEqualTo(ScryDisclosureContentKind.Schema);
            await Assert.That(Encoding.UTF8.GetString(schema.Units.Single().Content.Bytes.Span)).IsEqualTo(ScryJson.Serialize(described));
            await Assert.That(await Disclosures.Events(store, "bob")).Count().IsEqualTo(1);
        }
    }

    // The SQL a query would run says more than its rows do: table names, the shape of every policy, the
    // values bound into it. Being shown it is recorded, with the text.
    [Test]
    public async Task ASqlPreviewIsRecordedWithItsText()
    {
        var (processor, store) = Disclosures.Audited();
        await using var context = TestContext.CreateSeeded();

        var sql = processor.ToQueryString(Disclosures.Names(), context, EmptyServiceProvider.Instance);

        var shown = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(shown.Event.Kind).IsEqualTo(ScryDisclosureKind.SqlPreview);
            await Assert.That(shown.Units.Single().Content.Kind).IsEqualTo(ScryDisclosureContentKind.Sql);
            await Assert.That(Encoding.UTF8.GetString(shown.Units.Single().Content.Bytes.Span)).IsEqualTo(sql);
            await Assert.That(shown.Shape!.Fields.Select(Disclosures.Field)).Contains("Employee.Name: Returned");
        }
    }

    // The entries of one batch are answers to one request, and say so.
    [Test]
    public async Task TheEntriesOfABatchAreTiedTogether()
    {
        var (processor, store) = Disclosures.Audited();
        var batch = QueryBatchRequest.Create(
        [
            Disclosures.Names(),
            QueryRequest.Create("Missing", [new CountOp()]),
            Disclosures.Names()
        ]);

        await using var context = TestContext.CreateSeeded();
        processor.ExecuteBatch(batch, context);
        await Disclosures.Buffered(processor, Disclosures.Names());

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(events).Count().IsEqualTo(3);
            await Assert.That(events[0].Event.Correlation).EndsWith("/0");
            await Assert.That(events[1].Event.Correlation).EndsWith("/2");
            await Assert.That(events[0].Event.Correlation![..^2]).IsEqualTo(events[1].Event.Correlation![..^2]);
            await Assert.That(events[2].Event.Correlation).IsNull();
        }
    }

    static AttachmentRequest Document(int id) =>
        AttachmentRequest.Create("Contract", "Document", [new(id.ToString(CultureInfo.InvariantCulture), ClrTypeTag.Int32)]);

    static async Task<List<string>> Lines(ScryProcessor processor, TestContext context)
    {
        var (_, _, rows) = processor.StreamBuffered(Disclosures.Names(), context, EmptyServiceProvider.Instance, new HeaderDictionary(), new HeaderDictionary());
        List<string> lines = [];
        await foreach (var row in rows)
        {
            lines.Add(Encoding.UTF8.GetString(row.Span));
        }

        return lines;
    }

    static ScryProcessor Live(IScryDisclosureSink sink) =>
        Disclosures.Audited(
            sink,
            options =>
            {
                options.MaxSubscriptions = 10;
                options.SubscriptionThrottle = TimeSpan.Zero;
                options.SubscriptionPollInterval = null;
            });

    static QueryRequest Regions(string where) =>
        Disclosures.From<Order>("Order")
            .Where(_ => _.Region == where)
            .OrderBy(_ => _.Id)
            .Select(_ => new
            {
                _.Region
            })
            .ToScryRequest();

    static async Task<SqlDatabase<TestContext>> Seeded(string name)
    {
        var database = await TestContext.CreateIsolated(name);
        await using var context = database.NewDbContext();
        context.Orders.AddRange(
            new()
            {
                Region = "North",
                Revision = 1
            },
            new()
            {
                Region = "South",
                Revision = 2
            });
        await context.SaveChangesAsync();
        return database;
    }

    static async Task Insert(SqlDatabase<TestContext> database, ScryProcessor processor, string region)
    {
        await using var writing = new TestContext(
            new DbContextOptionsBuilder<TestContext>()
                .UseSqlServer(database.ConnectionString)
                .AddInterceptors(new ScryChangeInterceptor(processor.Changes))
                .Options);
        writing.Orders.Add(
            new()
            {
                Region = region,
                Revision = 10
            });
        await writing.SaveChangesAsync();
    }

    static ServiceProvider Services(IScryAuditor auditor)
    {
        var services = new ServiceCollection();
        services.AddSingleton(auditor);
        return services.BuildServiceProvider();
    }

    sealed class RecordingAuditor :
        IScryAuditor
    {
        public List<ScryAuditEntry> Entries { get; } = [];

        public void Record(ScryAuditEntry entry) =>
            Entries.Add(entry);
    }

    // Counts runs by the audit entries they leave, which is one per run whether or not it spoke.
    sealed class RunCounter :
        IScryAuditor
    {
        List<ScryAuditEntry> entries = [];

        public IReadOnlyList<ScryAuditEntry> Entries
        {
            get
            {
                lock (entries)
                {
                    return [.. entries];
                }
            }
        }

        public void Record(ScryAuditEntry entry)
        {
            lock (entries)
            {
                entries.Add(entry);
            }
        }

        public async Task Reaches(int count)
        {
            var started = Stopwatch.GetTimestamp();
            while (Entries.Count < count)
            {
                if (Stopwatch.GetElapsedTime(started) > patience)
                {
                    Assert.Fail($"Expected {count} runs; saw {Entries.Count}.");
                }

                await Task.Delay(20);
            }
        }
    }
}
