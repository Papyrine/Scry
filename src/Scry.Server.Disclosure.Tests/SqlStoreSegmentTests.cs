using static SqlStores;

/// <summary>
/// The record kept in segments, as a log is kept in files: one written to, a new one started when the
/// last has had its time or reached its size, and the oldest retired when there are more than are to
/// be kept. What is asked of the record is asked of every segment there is, and the chain of hashes
/// goes on through all of them, the retired ones included.
/// </summary>
public class SqlStoreSegmentTests
{
    // Answers given before and after a new segment is started are read as one record, and say what
    // the in-memory store says of the same answers. Each is in the segment that was being written to
    // when it was given, and the chain links them across the two.
    [Test]
    public async Task TheRecordIsReadAcrossEverySegment()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var who = new Who();
        var processor = Processor(new Both(memory, kept), who);

        processor.Execute(Clinic.Names(), database.Context);
        processor.Execute(Clinic.All("Ward", "Name"), database.Context);
        await kept.DrainAsync();
        var started = await kept.RollAsync();
        who.Name = "nurse.kim";
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();

        var segments = await kept.SegmentsAsync();
        var check = await kept.VerifyChainAsync();
        using (Assert.Multiple())
        {
            await Assert.That(await Told(kept)).IsEqualTo(await Told(memory));
            await Assert.That(started.Number).IsEqualTo(2);
            await Assert.That(segments.Select(_ => _.Schema)).IsEquivalentTo(["scry_000001", "scry_000002"], CollectionOrdering.Matching);
            await Assert.That(segments[0].Sealed).IsNotNull();
            await Assert.That(segments[0].Link).IsEqualTo(2);
            await Assert.That(segments[1].Sealed).IsNull();
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry_000001].[DisclosureEvent]")).IsEqualTo(2);
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry_000002].[DisclosureEvent]")).IsEqualTo(1);
            await Assert.That(await Count(database, "DisclosureEvent")).IsEqualTo(3);
            await Assert.That(check.Intact).IsTrue();
            await Assert.That(check.Records).IsEqualTo(3);
            await Assert.That((await kept.Status()).Events).IsEqualTo(3);
        }
    }

    // With two kept, starting a third retires the first: its tables are dropped, and what it held
    // can no longer be asked. What stays is a line saying when it went and how much it held, and its
    // links in the chain, which still holds from end to end.
    [Test]
    public async Task TheOldestSegmentIsRetiredWhenThereAreMoreThanAreKept()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database, _ => _.Keep = 2);
        var who = new Who();
        var processor = Processor(kept, who);

        processor.Execute(Clinic.Names(), database.Context);
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        await kept.RollAsync();
        who.Name = "nurse.kim";
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        var before = await kept.ReceiversOf("Patient", [1]).ToListAsync();
        await kept.RollAsync();

        var segments = await kept.SegmentsAsync();
        var after = await kept.ReceiversOf("Patient", [1]).ToListAsync();
        var check = await kept.VerifyChainAsync();
        using (Assert.Multiple())
        {
            await Assert.That(before).Count().IsEqualTo(3);
            await Assert.That(after).Count().IsEqualTo(1);
            await Assert.That(after[0].Event.Caller).IsEqualTo("nurse.kim");
            await Assert.That(await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).IsEmpty();
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM sys.schemas WHERE [name] = N'scry_000001'")).IsEqualTo(0);
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM sys.schemas WHERE [name] LIKE N'scry[_]%'")).IsEqualTo(2);
            await Assert.That(segments).Count().IsEqualTo(3);
            await Assert.That(segments[0].Retired).IsNotNull();
            await Assert.That(segments[0].HandedOn).IsFalse();
            await Assert.That(segments[0].Events).IsEqualTo(2);
            await Assert.That(segments[0].Batches).IsEqualTo(2);
            await Assert.That(segments[0].Bytes!.Value).IsGreaterThan(0);
            await Assert.That(segments[0].From).IsNotNull();
            await Assert.That(segments[1].Retired).IsNull();
            await Assert.That(await Count(database, "DisclosureChain")).IsEqualTo(3);
            await Assert.That(check.Intact).IsTrue();
            await Assert.That(check.Records).IsEqualTo(3);
            await Assert.That((await kept.Status()).Events).IsEqualTo(1);
        }
    }

    // A segment is handed on before it is dropped, as every batch it was made from. Put into another
    // store, the copy says what the record said while the segment was in it, and each batch still
    // makes the digest the chain holds for it.
    [Test]
    public async Task ARetiredSegmentIsHandedOnAsWhatItWasMadeFrom()
    {
        await using var database = await Clinic.Instance.Build();
        var first = new ScryMemoryDisclosureStore();
        var copy = new MemoryStream();
        ScryDisclosureSegment? handed = null;
        await using var kept = Store(
            database,
            _ =>
            {
                _.Keep = 1;
                _.Retire = async (archive, cancel) =>
                {
                    handed = archive.Segment;
                    await archive.WriteTo(copy, cancel);
                };
            });
        var who = new Who();

        var early = Processor(new Both(first, kept), who);
        early.Execute(Clinic.Names(), database.Context);
        early.Execute(Clinic.All("Ward", "Name"), database.Context);
        await kept.DrainAsync();
        await kept.RollAsync();

        copy.Position = 0;
        var restored = new ScryMemoryDisclosureStore();
        var digests = new List<string>();
        await foreach (var batch in ScryDisclosureArchive.Read(copy))
        {
            await restored.AppendAsync(batch, Cancel.None);
            digests.Add(Convert.ToHexString(DisclosureChain.Digest(batch)));
        }

        var segments = await kept.SegmentsAsync();
        using (Assert.Multiple())
        {
            await Assert.That(await Told(restored)).IsEqualTo(await Told(first));
            await Assert.That(digests).Count().IsEqualTo(2);
            await Assert.That(digests[0]).IsEqualTo(await Scalar<string>(database, "SELECT CONVERT(varchar(64), [Digest], 2) FROM [scry].[DisclosureChain] WHERE [Link] = 1"));
            await Assert.That(digests[1]).IsEqualTo(await Scalar<string>(database, "SELECT CONVERT(varchar(64), [Digest], 2) FROM [scry].[DisclosureChain] WHERE [Link] = 2"));
            await Assert.That(handed!.Number).IsEqualTo(1);
            await Assert.That(handed.Events).IsEqualTo(2);
            await Assert.That(segments[0].HandedOn).IsTrue();
            await Assert.That(segments[0].Retired).IsNotNull();
            await Assert.That((await kept.Status()).Events).IsEqualTo(0);
        }
    }

    // A segment that could not be handed on is not dropped. It goes on being read, nothing more is
    // written to it, and it is handed on again the next time the record is moved on.
    [Test]
    public async Task ASegmentThatCouldNotBeHandedOnIsKept()
    {
        await using var database = await Clinic.Instance.Build();
        var failing = true;
        var asked = 0;
        await using var kept = Store(
            database,
            _ =>
            {
                _.Keep = 1;
                _.Retire = (_, _) =>
                {
                    asked++;
                    if (failing)
                    {
                        throw new IOException("The archive is not there.");
                    }

                    return Task.CompletedTask;
                };
            });
        var processor = Processor(kept, new());

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        var refused = await Assert.ThrowsAsync<IOException>(() => kept.RollAsync());
        var held = await kept.SegmentsAsync();
        var still = await kept.ReceiversOf("Patient", [1]).ToListAsync();

        failing = false;
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        var gone = await kept.SegmentsAsync();

        using (Assert.Multiple())
        {
            await Assert.That(refused!.Message).IsEqualTo("The archive is not there.");
            await Assert.That(held[0].Retiring).IsTrue();
            await Assert.That(held[0].Retired).IsNull();
            await Assert.That(still).Count().IsEqualTo(1);
            await Assert.That(asked).IsEqualTo(2);
            await Assert.That(gone[0].Retired).IsNotNull();
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry_000002].[DisclosureEvent]")).IsEqualTo(1);
            await Assert.That((await kept.ReceiversOf("Patient", [1]).ToListAsync())).Count().IsEqualTo(1);
        }
    }

    // Like a log file, the record rolls when something is about to be added to it and the segment
    // being written to has had its time.
    [Test]
    public async Task ASegmentThatHasHadItsTimeIsRolledAwayFrom()
    {
        await using var database = await Clinic.Instance.Build();
        var clock = new Shifted();
        await using var kept = Store(
            database,
            _ =>
            {
                _.RollAfter = TimeSpan.FromDays(1);
                _.Clock = clock;
            });
        var processor = Processor(kept, new());

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        clock.By = TimeSpan.FromHours(20);
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        var early = await kept.SegmentsAsync();
        clock.By = TimeSpan.FromHours(30);
        await kept.DrainAsync();
        var idle = await kept.SegmentsAsync();
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(early).Count().IsEqualTo(1);

            // Nothing was added, so nothing was rolled.
            await Assert.That(idle).Count().IsEqualTo(1);
            await Assert.That(await kept.SegmentsAsync()).Count().IsEqualTo(2);
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry_000001].[DisclosureEvent]")).IsEqualTo(2);
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry_000002].[DisclosureEvent]")).IsEqualTo(1);
        }
    }

    // Or has reached its size.
    [Test]
    public async Task ASegmentThatHasReachedItsSizeIsRolledAwayFrom()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database, _ => _.RollAtBytes = 1);
        var processor = Processor(kept, new());

        foreach (var _ in Enumerable.Range(0, 3))
        {
            processor.Execute(Clinic.Names(), database.Context);
            await kept.DrainAsync();
        }

        using (Assert.Multiple())
        {
            // The first went into a segment that held nothing, and each after it found one that did.
            await Assert.That(await kept.SegmentsAsync()).Count().IsEqualTo(3);
            await Assert.That((await kept.ReceiversOf("Patient", [1]).ToListAsync())).Count().IsEqualTo(3);
        }
    }

    // An answer that arrives in pieces stays in the segment it began in, though a new one was started
    // before its end: so an answer is in one segment, and leaves with it.
    [Test]
    public async Task AnAnswerInPiecesStaysInTheSegmentItBeganIn()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var both = new Both(memory, kept);
        var ada = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Ada\"}");
        var brook = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Brook\"}");
        var names = Clinic.Shape(new ScryDisclosureField("Patient", "Name", ScryDisclosureFieldUse.Returned, false));
        var id = Guid.CreateVersion7();
        var begin = new ScryDisclosureEvent(id, DateTimeOffset.UtcNow, ScryDisclosureKind.Stream, "Patient")
        {
            Caller = "dr.osei",
            Shape = names.Address,
            Node = "ward-1"
        };

        await both.AppendAsync(Clinic.Part(id, 0, begin, names), Cancel.None);
        await both.AppendAsync(Clinic.Part(id, 1, rows: [(0, ada, "Patient", "[1]")]), Cancel.None);
        await kept.DrainAsync();
        await kept.RollAsync();
        await both.AppendAsync(Clinic.Part(id, 2, rows: [(1, brook, "Patient", "[2]")]), Cancel.None);
        await both.AppendAsync(Clinic.Part(id, 3, close: new(ScryDisclosureOutcome.Released, 2)), Cancel.None);
        await kept.DrainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(await Told(kept, "dr.osei")).IsEqualTo(await Told(memory, "dr.osei"));
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry_000001].[DisclosureBatch]")).IsEqualTo(4);
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry_000002].[DisclosureBatch]")).IsEqualTo(0);
            await Assert.That((await kept.VerifyChainAsync()).Intact).IsTrue();
        }
    }

    // What was sent of a row is erased from every segment it was sent in, and counted once.
    [Test]
    public async Task AnErasureReachesEverySegment()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var processor = Processor(new Both(memory, kept), new());

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        await kept.RollAsync();
        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();

        var erased = await kept.EraseAsync("Patient", [1], by: "records.officer");
        await memory.EraseAsync("Patient", [1], by: "records.officer");

        using (Assert.Multiple())
        {
            await Assert.That(erased.Units).IsEqualTo(1);
            await Assert.That(await Scalar<int>(database, "SELECT COUNT(*) FROM [scry].[DisclosureContent] WHERE [Erased] = 1")).IsEqualTo(2);
            await Assert.That(await Told(kept)).IsEqualTo(await Told(memory));
            await Assert.That((await kept.VerifyChainAsync()).Intact).IsTrue();
        }
    }

    // A question about a span of time is asked only of the segments that cover any of it, and is
    // answered as it would be asked of all of them: whatever the span, the answer is the in-memory
    // store's, which has no segments to leave out.
    [Test]
    public async Task AQuestionAboutASpanOfTimeIsAnsweredTheSameWhicheverSegmentsItReaches()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var processor = Processor(new Both(memory, kept), new());

        foreach (var segment in Enumerable.Range(0, 3))
        {
            processor.Execute(Clinic.Names(), database.Context);
            await Task.Delay(20);
            processor.Execute(Clinic.Names(), database.Context);
            await kept.DrainAsync();
            if (segment < 2)
            {
                await kept.RollAsync();
            }

            await Task.Delay(20);
        }

        var all = await memory.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        var times = all.Select(_ => _.Event.At).Order().ToList();
        var segments = await kept.SegmentsAsync();
        (DateTimeOffset From, DateTimeOffset To)[] spans =
        [
            (times[0], times[1]),
            (times[1], times[2]),
            (times[2], times[3]),
            (times[4], times[5]),
            (times[0], times[5]),
            (times[1].AddTicks(1), times[2].AddTicks(-1)),
            (times[0].AddDays(-2), times[0].AddDays(-1)),
            (times[5].AddDays(1), times[5].AddDays(2))
        ];

        using (Assert.Multiple())
        {
            await Assert.That(all).Count().IsEqualTo(6);
            await Assert.That(segments[0].From).IsEqualTo(times[0]);
            await Assert.That(segments[0].To).IsEqualTo(times[1]);
            await Assert.That(segments[1].From).IsEqualTo(times[2]);
            await Assert.That(segments[2].From).IsNull();
            foreach (var (from, to) in spans)
            {
                var told = await memory.ReceivedBy("dr.osei", from, to).ToListAsync();
                var read = await kept.ReceivedBy("dr.osei", from, to).ToListAsync();
                var sent = await memory.ReceiversOf("Patient", [1], from, to).ToListAsync();
                var found = await kept.ReceiversOf("Patient", [1], from, to).ToListAsync();
                await Assert.That(read.Select(_ => _.Event.Id)).IsEquivalentTo(told.Select(_ => _.Event.Id), CollectionOrdering.Matching);
                await Assert.That(found.Select(_ => _.Event.Id)).IsEquivalentTo(sent.Select(_ => _.Event.Id), CollectionOrdering.Matching);
            }
        }
    }

    // A store told to catch up before reading finds an answer the moment it was accepted, with no
    // move asked for: the question waits for it.
    [Test]
    public async Task AQuestionCanWaitForWhatWasJustAccepted()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database, _ => _.CatchUpBeforeReading = true);
        var batch = Clinic.Batch(1);
        await kept.AppendAsync(batch, Cancel.None);

        var found = await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();

        await Assert.That(found.Select(_ => _.Event.Id)).IsEquivalentTo([batch.EventId]);
    }

    // A host that only reads does not move the record on, so catching up waits for the node that
    // does, and gives up after the accept's own timeout where none does.
    [Test]
    public async Task AHostThatOnlyReadsWaitsForTheNodeThatMoves()
    {
        await using var database = await Clinic.Instance.Build();
        await using var serving = Store(database);

        // Two hosts that only read: one that gives up soon, to see it give up, and one that waits as
        // long as a move on a busy machine can take, to see it see the move.
        await using var impatient = Reader(database, TimeSpan.FromSeconds(3));
        await using var patient = Reader(database, TimeSpan.FromMinutes(1));
        await serving.AppendAsync(Clinic.Batch(1), Cancel.None);

        var timedOut = await Assert.ThrowsAsync<TimeoutException>(() => impatient.CatchUpAsync());
        var waiting = (await serving.Status()).Pending;
        var caught = patient.CatchUpAsync();
        await serving.DrainAsync();
        await caught;

        using (Assert.Multiple())
        {
            await Assert.That(timedOut).IsNotNull();
            await Assert.That(waiting).IsEqualTo(1);
            await Assert.That((await serving.Status()).Pending).IsEqualTo(0);
        }
    }

    static ScrySqlServerDisclosureStore Reader(SqlDatabase<ClinicContext> database, TimeSpan patience) =>
        new(
            new()
            {
                ConnectionString = database.ConnectionString,
                DrainInterval = null,
                AcceptTimeout = patience
            })
        {
            ReadsOnly = true
        };

    // Every node that moves the record goes by the settings the record holds, which are the last
    // node to start's: a node configured to keep three retires down to the one a later node keeps.
    [Test]
    public async Task EveryNodeGoesByTheRecordsSettings()
    {
        await using var database = await Clinic.Instance.Build();
        await using var first = Store(database, _ => _.Keep = 3);
        await using var second = Store(database, _ => _.Keep = 1);
        var processor = Processor(first, new());

        processor.Execute(Clinic.Names(), database.Context);
        await first.DrainAsync();
        var before = await first.RetentionAsync();
        await first.RollAsync();
        var kept = (await first.SegmentsAsync()).Count(_ => _.Retired is null);

        processor.Execute(Clinic.Names(), database.Context);
        await second.DrainAsync();
        var after = await first.RetentionAsync();
        await first.RollAsync();
        var segments = await first.SegmentsAsync();

        using (Assert.Multiple())
        {
            await Assert.That(before.Keep).IsEqualTo(3);
            await Assert.That(kept).IsEqualTo(2);
            await Assert.That(after.Keep).IsEqualTo(1);
            await Assert.That(segments.Count(_ => _.Retired is null)).IsEqualTo(1);
        }
    }

    // A node with nothing to hand a segment to neither turns handing on off for the others nor
    // drops a segment itself: what is retired is retired with a copy, or not at all.
    [Test]
    public async Task ANodeWithNoArchiveDropsNothingTheRecordHandsOn()
    {
        await using var database = await Clinic.Instance.Build();
        var handed = 0;
        await using var archiving = Store(
            database,
            _ =>
            {
                _.Keep = 1;
                _.Retire = (_, _) =>
                {
                    handed++;
                    return Task.CompletedTask;
                };
            });
        await using var bare = Store(database, _ => _.Keep = 1);
        var processor = Processor(archiving, new());

        processor.Execute(Clinic.Names(), database.Context);
        await archiving.DrainAsync();
        processor.Execute(Clinic.Names(), database.Context);
        await bare.DrainAsync();
        await bare.RollAsync();
        var kept = (await bare.SegmentsAsync()).Count(_ => _.Retired is null);
        var said = (await bare.Status()).Problem;
        await archiving.RetireAsync();

        using (Assert.Multiple())
        {
            await Assert.That((await bare.RetentionAsync()).HandsOn).IsTrue();
            await Assert.That(kept).IsEqualTo(2);
            await Assert.That(said).Contains("no ScrySqlServerDisclosureOptions.Retire");
            await Assert.That(handed).IsEqualTo(1);
            await Assert.That((await bare.SegmentsAsync()).Count(_ => _.Retired is null)).IsEqualTo(1);
        }
    }

    // A segment is kept for as long as it is to be, counted from the last answer in it, and is then
    // retired however few are kept. The one being written to is kept whatever its age.
    [Test]
    public async Task ASegmentKeptAsLongAsItIsToBeIsRetired()
    {
        await using var database = await Clinic.Instance.Build();
        var clock = new Shifted();
        await using var kept = Store(
            database,
            _ =>
            {
                _.KeepFor = TimeSpan.FromDays(30);
                _.Clock = clock;
            });
        var processor = Processor(kept, new());

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        await kept.RollAsync();
        clock.By = TimeSpan.FromDays(29);
        var young = await kept.RetireAsync();
        clock.By = TimeSpan.FromDays(31);
        var old = await kept.RetireAsync();
        clock.By = TimeSpan.FromDays(400);
        var current = await kept.RetireAsync();
        var segments = await kept.SegmentsAsync();

        using (Assert.Multiple())
        {
            await Assert.That(young).IsEqualTo(0);
            await Assert.That(old).IsEqualTo(1);
            await Assert.That(current).IsEqualTo(0);
            await Assert.That(segments[0].Retired).IsNotNull();
            await Assert.That(segments[1].Retired).IsNull();
            await Assert.That((await kept.RetentionAsync()).KeepFor).IsEqualTo(TimeSpan.FromDays(30));
        }
    }

    // The whole chain is checked a stretch at a time, each stretch picking up where the one before
    // ended, and finds a change made anywhere along it.
    [Test]
    public async Task TheWholeChainIsCheckedAStretchAtATime()
    {
        await using var database = await Clinic.Instance.Build();
        await using var kept = Store(database);
        foreach (var index in Enumerable.Range(0, 7))
        {
            await kept.AppendAsync(Clinic.Batch(index), Cancel.None);
        }

        await kept.DrainAsync();
        var reported = new List<long>();
        var whole = await kept.VerifyWholeChainAsync(new Collect(reported), stretch: 3);
        await SqlStores.Execute(database, "UPDATE [scry_000001].[DisclosureEvent] SET [Caller] = N'somebody.else' WHERE [EventId] = (SELECT [EventId] FROM [scry_000001].[DisclosureBatch] WHERE [Link] = 6)");
        var broken = await kept.VerifyWholeChainAsync(stretch: 3);

        using (Assert.Multiple())
        {
            await Assert.That(whole.Intact).IsTrue();
            await Assert.That(whole.Records).IsEqualTo(7);
            await Assert.That(reported).IsEquivalentTo([3L, 6L, 7L], CollectionOrdering.Matching);
            await Assert.That(broken.Intact).IsFalse();
            await Assert.That(broken.BrokenAt).IsEqualTo(6);
            await Assert.That((await kept.Status()).LastVerification!.BrokenAt).IsEqualTo(6);
        }
    }

    // Progress reported as it happens, rather than through a captured context.
    sealed class Collect(List<long> into) :
        IProgress<long>
    {
        public void Report(long value) =>
            into.Add(value);
    }

    // What asked on the caller's behalf is recorded beside the caller, kept by the store, read back
    // with the answer, and part of what the chain's link for it covers.
    [Test]
    public async Task WhatAskedOnTheCallersBehalfIsRecorded()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var processor = Processor(
            new Both(memory, kept),
            new(),
            _ =>
            {
                _.Agent = _ => "ward-assistant";
                _.Version<Patient>(_ => _.WardId);
            });

        processor.Execute(Clinic.Names(), database.Context);
        await kept.DrainAsync();
        var listed = await kept.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        var told = await memory.ReceivedBy("dr.osei", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        var rebuilt = await kept.Reconstruct(listed[0].Event.Id);
        var row = await kept.ReceiversOf("Patient", [1]).ToListAsync();
        var intact = (await kept.VerifyChainAsync()).Intact;
        var toldKept = await Told(kept);
        var toldMemory = await Told(memory);
        await SqlStores.Execute(database, "UPDATE [scry_000001].[DisclosureEvent] SET [Agent] = N'somebody.else'");

        using (Assert.Multiple())
        {
            await Assert.That(told[0].Event.Agent).IsEqualTo("ward-assistant");
            await Assert.That(listed[0].Event.Agent).IsEqualTo("ward-assistant");
            await Assert.That(rebuilt!.Event.Agent).IsEqualTo("ward-assistant");
            await Assert.That(row[0].Event.Agent).IsEqualTo("ward-assistant");
            await Assert.That(row[0].Ordinal).IsEqualTo(0);
            await Assert.That(row[0].Version).IsNotNull();
            await Assert.That(rebuilt.Units[0].Entities[0].Version).IsEqualTo(row[0].Version);
            await Assert.That(told[0].Event.Id).IsEqualTo(listed[0].Event.Id);
            await Assert.That(toldKept).IsEqualTo(toldMemory);
            await Assert.That(toldKept).Contains("through ward-assistant");
            await Assert.That(intact).IsTrue();
            await Assert.That((await kept.VerifyChainAsync()).Intact).IsFalse();
        }
    }

    // A clock that can be put forward, and is otherwise the machine's: the first segment is opened by
    // the database, at the database's own time.
    sealed class Shifted :
        TimeProvider
    {
        public TimeSpan By { get; set; }

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UtcNow + By;
    }
}
