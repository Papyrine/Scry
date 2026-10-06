/// <summary>
/// The local journal: what it accepts reaches the store behind it, in order, whatever happens in
/// between — the store being down, the process stopping, a write the stop cut short — and what it
/// cannot make good it refuses rather than hides.
/// </summary>
public class DisclosureJournalTests
{
    static TimeSpan patience = TimeSpan.FromSeconds(30);
    static DateTimeOffset noon = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task WhatIsAcceptedReachesTheStoreInOrder()
    {
        using var directory = new JournalDirectory();
        var store = new ScryMemoryDisclosureStore();
        var tap = new TappedSink(store);
        var batches = Enumerable.Range(0, 40).Select(Batch).ToList();

        await using (var journal = new ScryDisclosureJournal(directory.Path, tap))
        {
            foreach (var batch in batches)
            {
                await journal.AppendAsync(batch, Cancel.None);
            }

            await journal.DrainAsync().WaitAsync(patience);
        }

        using (Assert.Multiple())
        {
            await Assert.That(tap.Batches.Select(_ => _.EventId)).IsEquivalentTo(batches.Select(_ => _.EventId), CollectionOrdering.Matching);
            await Assert.That((await store.Status()).Events).IsEqualTo(40);

            // What was shipped is what was handed over, content and all.
            var first = (await store.Reconstruct(batches[0].EventId))!;
            await Assert.That(Encoding.UTF8.GetString(first.Units.Single().Content.Bytes.Span)).IsEqualTo("{\"row\":0}");
        }
    }

    // Many callers at once: every answer waits for a flush, and none of them waits for its own.
    // Each caller's batches still arrive in the order it handed them over.
    [Test]
    public async Task ManyAtOnceAllArriveEachInItsOwnOrder()
    {
        using var directory = new JournalDirectory();
        var tap = new TappedSink(new ScryMemoryDisclosureStore());
        await using var journal = new ScryDisclosureJournal(directory.Path, tap);

        var callers = Enumerable.Range(0, 16).Select(_ => Guid.CreateVersion7()).ToList();
        await Task.WhenAll(
            callers.Select(
                id => Task.Run(
                    async () =>
                    {
                        for (var sequence = 0; sequence < 25; sequence++)
                        {
                            await journal.AppendAsync(
                                new()
                                {
                                    EventId = id,
                                    Sequence = sequence
                                },
                                Cancel.None);
                        }
                    })));
        await journal.DrainAsync().WaitAsync(patience);

        await Assert.That(tap.Batches).Count().IsEqualTo(400);
        foreach (var id in callers)
        {
            await Assert.That(tap.Batches.Where(_ => _.EventId == id).Select(_ => _.Sequence))
                .IsEquivalentTo(Enumerable.Range(0, 25), CollectionOrdering.Matching);
        }
    }

    // The point of a journal: the store behind being down costs a caller nothing. The answer is
    // accepted, the backlog says so, and the store is caught up once it is back.
    [Test]
    public async Task AStoreThatIsDownCostsACallerNothing()
    {
        using var directory = new JournalDirectory();
        var store = new ScryMemoryDisclosureStore();
        var behind = new SwitchedSink(store)
        {
            Down = true
        };
        await using var journal = new ScryDisclosureJournal(directory.Path, behind, Quick());

        for (var index = 0; index < 5; index++)
        {
            journal.Append(Batch(index));
        }

        var waiting = await journal.Status();
        behind.Down = false;
        await journal.DrainAsync().WaitAsync(patience);
        var caughtUp = await journal.Status();

        using (Assert.Multiple())
        {
            await Assert.That(waiting.Pending).IsEqualTo(5);
            await Assert.That(waiting.PendingBytes).IsGreaterThan(0);
            await Assert.That(waiting.OldestPending).IsNotNull();
            await Assert.That(waiting.Events).IsEqualTo(0);
            await Assert.That(caughtUp.Pending).IsEqualTo(0);
            await Assert.That(caughtUp.PendingBytes).IsEqualTo(0);
            await Assert.That(caughtUp.OldestPending).IsNull();
            await Assert.That(caughtUp.Events).IsEqualTo(5);
        }
    }

    // What was accepted was on disk before anybody was told so, so a process that stops there has
    // lost nothing: the next one to open the directory ships it.
    [Test]
    public async Task WhatWasAcceptedSurvivesTheProcessStopping()
    {
        using var directory = new JournalDirectory();
        var batches = Enumerable.Range(0, 12).Select(Batch).ToList();
        await Stopped(directory, batches);

        var store = new ScryMemoryDisclosureStore();
        var tap = new TappedSink(store);
        await using var journal = new ScryDisclosureJournal(directory.Path, tap);
        var found = await journal.Status();
        await journal.DrainAsync().WaitAsync(patience);

        using (Assert.Multiple())
        {
            await Assert.That(found.Pending).IsEqualTo(12);
            await Assert.That(tap.Batches.Select(_ => _.EventId)).IsEquivalentTo(batches.Select(_ => _.EventId), CollectionOrdering.Matching);
            await Assert.That((await journal.Status()).Pending).IsEqualTo(0);
        }
    }

    // A crash part-way through a write leaves a group that is not whole at the very end of the
    // newest segment. Nothing in it was acknowledged — acknowledging follows the flush — so it is
    // dropped, and everything before it is shipped.
    [Test]
    [Arguments("cut")]
    [Arguments("zeros")]
    [Arguments("garbled")]
    public async Task AWriteTheStopCutShortIsDropped(string damage)
    {
        using var directory = new JournalDirectory();
        var batches = Enumerable.Range(0, 3).Select(Batch).ToList();
        await Stopped(directory, batches);
        var segment = directory.Segments().Single();
        var whole = new FileInfo(segment).Length;
        var groups = Groups(segment);

        using (var file = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite))
        {
            switch (damage)
            {
                // The last group lost its end.
                case "cut":
                    file.SetLength(whole - 5);
                    break;
                // Room was made for another group and nothing was written into it.
                case "zeros":
                    file.Seek(0, SeekOrigin.End);
                    file.Write(new byte[300]);
                    break;
                // The last group is all there and wrong.
                default:
                    file.Seek(groups[^1].Offset + 30, SeekOrigin.Begin);
                    file.WriteByte(0xFF);
                    break;
            }
        }

        var tap = new TappedSink();
        await using var journal = new ScryDisclosureJournal(directory.Path, tap);
        await journal.DrainAsync().WaitAsync(patience);

        var kept = batches.Count;
        if (damage != "zeros")
        {
            kept--;
        }

        using (Assert.Multiple())
        {
            await Assert.That(tap.Batches.Select(_ => _.EventId)).IsEquivalentTo(batches.Take(kept).Select(_ => _.EventId), CollectionOrdering.Matching);

            // And the journal goes on from where the whole groups end.
            await journal.AppendAsync(Batch(99), Cancel.None);
            await journal.DrainAsync().WaitAsync(patience);
            await Assert.That(tap.Batches).Count().IsEqualTo(kept + 1);
        }
    }

    // Damage with intact records after it is not a write that was cut short: those records were
    // flushed and acknowledged after it, so it was whole once. Forgetting acknowledged records to get
    // started is the one thing a journal must not do, so it refuses and says what to do.
    [Test]
    public async Task DamageBeforeIntactRecordsRefusesToOpen()
    {
        using var directory = new JournalDirectory();
        await Stopped(directory, [.. Enumerable.Range(0, 3).Select(Batch)]);
        var segment = directory.Segments().Single();
        var first = Groups(segment)[0];
        using (var file = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Seek(first.Offset + 30, SeekOrigin.Begin);
            file.WriteByte(0xFF);
        }

        var exception = Assert.ThrowsExactly<ScryDisclosureException>(
            () => new ScryDisclosureJournal(directory.Path, new TappedSink()));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("is damaged at byte 16");
            await Assert.That(exception.Message).Contains("move the segment aside");

            // Refusing lets go of the directory, so whoever fixes it can open it.
            await Assert.That(() => File.Open(Path.Combine(directory.Path, "journal.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose()).ThrowsNothing();
        }
    }

    [Test]
    public async Task AMissingSegmentRefusesToOpen()
    {
        using var directory = new JournalDirectory();
        await Stopped(directory, [.. Enumerable.Range(0, 30).Select(Batch)], segmentBytes: 400);
        var segments = directory.Segments();
        await Assert.That(segments.Count).IsGreaterThan(2);
        File.Delete(segments[1]);

        var exception = Assert.ThrowsExactly<ScryDisclosureException>(
            () => new ScryDisclosureJournal(directory.Path, new TappedSink()));

        await Assert.That(exception.Message).Contains("records between them are missing");
    }

    // A journal that is keeping up holds about one segment: each is removed once the store behind
    // has everything in it.
    [Test]
    public async Task ASegmentIsRemovedOnceItIsShipped()
    {
        using var directory = new JournalDirectory();
        var tap = new TappedSink();
        await using var journal = new ScryDisclosureJournal(directory.Path, tap, Quick(segmentBytes: 400));

        for (var index = 0; index < 30; index++)
        {
            await journal.AppendAsync(Batch(index), Cancel.None);
        }

        await journal.DrainAsync().WaitAsync(patience);

        using (Assert.Multiple())
        {
            await Assert.That(tap.Batches).Count().IsEqualTo(30);
            await Assert.That(directory.Segments()).Count().IsEqualTo(1);
        }
    }

    // A store that stays down is an outage to be told about, not one to absorb until the disk is
    // full. Past its budget the journal accepts nothing, and an answer it does not accept is not sent.
    [Test]
    public async Task AFullJournalAcceptsNothing()
    {
        using var directory = new JournalDirectory();
        var behind = new SwitchedSink(new ScryMemoryDisclosureStore())
        {
            Down = true
        };
        await using var journal = new ScryDisclosureJournal(directory.Path, behind, Quick(maxBytes: 600));

        var accepted = 0;
        var refused = Assert.ThrowsExactly<ScryDisclosureException>(
            () =>
            {
                for (var index = 0; index < 100; index++)
                {
                    journal.Append(Batch(index));
                    accepted++;
                }
            });

        // Room again once the store has taken what was waiting.
        behind.Down = false;
        await journal.DrainAsync().WaitAsync(patience);
        journal.Append(Batch(100));

        using (Assert.Multiple())
        {
            await Assert.That(accepted).IsGreaterThan(0);
            await Assert.That(accepted).IsLessThan(100);
            await Assert.That(refused.Message).Contains("may hold 600");
        }
    }

    [Test]
    public async Task ADirectoryBelongsToOneProcess()
    {
        using var directory = new JournalDirectory();
        await using var journal = new ScryDisclosureJournal(directory.Path, new TappedSink());

        var exception = Assert.ThrowsExactly<ScryDisclosureException>(
            () => new ScryDisclosureJournal(directory.Path, new TappedSink()));

        await Assert.That(exception.Message).Contains("held by another process");
    }

    // The note of how far shipping got is written after the store accepted, so a stop between the two
    // ships again. That is why a store keeps a batch once however often it is handed it.
    [Test]
    public async Task WhatIsShippedAgainIsKeptOnce()
    {
        using var directory = new JournalDirectory();
        var store = new ScryMemoryDisclosureStore();
        var tap = new TappedSink(store);
        var batches = Enumerable.Range(0, 4).Select(Batch).ToList();
        var first = new ScryDisclosureJournal(directory.Path, tap);
        foreach (var batch in batches)
        {
            await first.AppendAsync(batch, Cancel.None);
        }

        await first.DrainAsync().WaitAsync(patience);
        await first.Abandon();
        File.Delete(Path.Combine(directory.Path, "checkpoint"));

        await using var second = new ScryDisclosureJournal(directory.Path, tap);
        await second.DrainAsync().WaitAsync(patience);

        using (Assert.Multiple())
        {
            await Assert.That(tap.Batches).Count().IsEqualTo(8);
            await Assert.That((await store.Status()).Events).IsEqualTo(4);
            await Assert.That((await store.Reconstruct(batches[0].EventId))!.Units).Count().IsEqualTo(1);
        }
    }

    // Closed, the journal accepts nothing, and says so rather than taking a record it will not keep.
    [Test]
    public async Task AClosedJournalAcceptsNothing()
    {
        using var directory = new JournalDirectory();
        var journal = new ScryDisclosureJournal(directory.Path, new TappedSink());
        await journal.DisposeAsync();
        await journal.DisposeAsync();

        var exception = Assert.ThrowsExactly<ScryDisclosureException>(() => journal.Append(Batch(0)));

        await Assert.That(exception.Message).Contains("closed");
    }

    // The whole path: an answer is recorded through the journal and reaches the store, and one the
    // journal cannot take is not given.
    [Test]
    public async Task AnAnswerIsRecordedThroughTheJournal()
    {
        using var directory = new JournalDirectory();
        var store = new ScryMemoryDisclosureStore();
        var behind = new SwitchedSink(store);
        await using var journal = new ScryDisclosureJournal(directory.Path, behind, Quick(maxBytes: 2_000));
        var processor = Disclosures.Audited(journal);

        await Disclosures.Buffered(processor, Disclosures.Names());
        await journal.DrainAsync().WaitAsync(patience);
        var recorded = await Disclosures.Events(store);

        behind.Down = true;
        var refused = 0;
        for (var index = 0; index < 20; index++)
        {
            try
            {
                await Disclosures.Buffered(processor, Disclosures.Names());
            }
            catch (ScryDisclosureException)
            {
                refused++;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(recorded.Single().Close!.Units).IsEqualTo(3);
            await Assert.That(refused).IsGreaterThan(0);
        }
    }

    // Asked for through the options, the journal is the container's: made with the host and closed
    // with it, which is what ships what is left when the host stops.
    [Test]
    public async Task AskedForThroughTheOptionsItIsTheHostsToClose()
    {
        using var directory = new JournalDirectory();
        var store = new ScryMemoryDisclosureStore();
        var services = new ServiceCollection();
        services.AddScry<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.UseDisclosureAudit(store, _ => _.UseJournal(directory.Path));
            });

        await using (var provider = services.BuildServiceProvider())
        {
            var sink = provider.GetRequiredService<IScryDisclosureSink>();
            await Assert.That(sink).IsTypeOf<ScryDisclosureJournal>();
            sink.Append(Batch(0));
        }

        // Closed with the provider: the directory is free again, and what was accepted was shipped.
        await using var again = new ScryDisclosureJournal(directory.Path, new TappedSink());
        await Assert.That((await store.Status()).Events).IsEqualTo(1);
    }

    static ScryDisclosureJournalOptions Quick(int segmentBytes = 8 << 20, long maxBytes = 1L << 30) =>
        new()
        {
            SegmentBytes = segmentBytes,
            MaxBytes = maxBytes,
            RetryDelay = TimeSpan.FromMilliseconds(10),
            MaxRetryDelay = TimeSpan.FromMilliseconds(50),
            DrainTimeout = TimeSpan.FromMilliseconds(200)
        };

    // Leaves a directory as a process that accepted these batches and then stopped dead would: all
    // of them on disk, none of them shipped. Each is handed over on its own, so each is a group.
    static async Task Stopped(JournalDirectory directory, List<ScryDisclosureBatch> batches, int segmentBytes = 8 << 20)
    {
        var journal = new ScryDisclosureJournal(
            directory.Path,
            new SwitchedSink(new TappedSink())
            {
                Down = true
            },
            Quick(segmentBytes));
        foreach (var batch in batches)
        {
            await journal.AppendAsync(batch, Cancel.None);
        }

        await journal.Abandon();
    }

    // One small answer: a row, the unit that carried it, and its close.
    static ScryDisclosureBatch Batch(int index)
    {
        var id = Guid.CreateVersion7(noon.AddSeconds(index));
        var bytes = Encoding.UTF8.GetBytes($$"""{"row":{{index}}}""");
        byte[] tagged = [(byte) ScryDisclosureContentKind.Row, .. bytes];
        var address = ScryDisclosureAddress.From(SHA256.HashData(tagged));
        return new()
        {
            EventId = id,
            Begin = new(id, noon.AddSeconds(index), ScryDisclosureKind.Single, "Employee")
            {
                Caller = "alice"
            },
            Units = [new(0, address)],
            Contents = [new(address, ScryDisclosureContentKind.Row, bytes.Length, bytes)],
            Close = new(ScryDisclosureOutcome.Released, 1)
        };
    }

    // Where each group of a segment begins, read the way the journal frames them: a sixteen-byte
    // segment header, then groups of a twenty-byte header, a payload, and eight bytes of checksum.
    static List<(long Offset, int Length)> Groups(string segment)
    {
        var bytes = File.ReadAllBytes(segment);
        var groups = new List<(long Offset, int Length)>();
        var offset = 16;
        while (offset + 28 <= bytes.Length)
        {
            var payload = BitConverter.ToInt32(bytes, offset + 4);
            var length = 20 + payload + 8;
            groups.Add((offset, length));
            offset += length;
        }

        return groups;
    }

    // A directory of a test's own, removed when the test is done with it.
    sealed class JournalDirectory :
        IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scry-journal-tests", Guid.NewGuid().ToString("N"));

        public List<string> Segments() =>
            [.. Directory.GetFiles(Path, "*.sdj").Order()];

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the operating system's own tidying of its temporary directory.
            }
        }
    }
}
