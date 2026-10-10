/// <summary>
/// The guarantee the disclosure audit makes: nothing recorded is handed on before the sink holds the
/// record of it, an answer the sink does not accept is not an answer, and what the record says about
/// an answer that then failed is what really happened to it.
/// </summary>
public class DisclosureFailClosedTests
{
    [Test]
    public async Task AnAnswerTheSinkRefusesIsNotGiven()
    {
        var sink = new RefusingSink();
        var processor = Disclosures.Audited(sink);
        var auditor = new RecordingAuditor();
        await using var services = Services(auditor);

        var written = await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            () => Disclosures.Buffered(processor, Disclosures.Names(), services));
        var direct = Assert.ThrowsExactly<ScryDisclosureException>(
            () => Disclosures.Direct(processor, Disclosures.Names(), services));

        // The trail says what the caller saw: a query that failed, and why — which the caller was
        // not told.
        using (Assert.Multiple())
        {
            await Assert.That(written!.Message).IsEqualTo("The disclosure audit did not accept the record of this answer, so it was not sent: The store is down.");
            await Assert.That(direct.Message).IsEqualTo(written.Message);
            await Assert.That(auditor.Entries.Select(_ => _.Outcome)).IsEquivalentTo(
                [ScryQueryOutcome.Failed, ScryQueryOutcome.Failed],
                CollectionOrdering.Matching);
            await Assert.That(auditor.Entries.All(_ => _.Disclosure is null)).IsTrue();
            await Assert.That(auditor.Entries[0].Error).IsEqualTo(written.Message);
        }
    }

    // A sink giving up on its own — a timeout of its own — throws the exception a caller going away
    // throws. Left as that, the query would be counted as abandoned, and a batch would be dropped whole
    // with the entries already answered. Nobody went away: the record was not accepted.
    [Test]
    public async Task ASinkGivingUpIsAFailureNotAnAbandonedRequest()
    {
        var processor = Disclosures.Audited(new RefusingSink(failure: () => new OperationCanceledException("The store timed out.")));
        var auditor = new RecordingAuditor();
        await using var services = Services(auditor);

        var exception = await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            () => Disclosures.Buffered(processor, Disclosures.Names(), services));

        using (Assert.Multiple())
        {
            await Assert.That(exception!.InnerException).IsTypeOf<OperationCanceledException>();
            await Assert.That(auditor.Entries.Single().Outcome).IsEqualTo(ScryQueryOutcome.Failed);
        }
    }

    // "Who received this" is the question the record exists to answer, so an answer nobody can be
    // named as having received is not sent unless the host said that is acceptable.
    [Test]
    public async Task AnAnswerForNobodyIsNotGiven()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Caller = _ => null);

        var exception = await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            () => Disclosures.Buffered(processor, Disclosures.Names()));

        using (Assert.Multiple())
        {
            await Assert.That(exception!.Message).Contains("has no caller");
            await Assert.That((await store.Status()).Events).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AnAnswerForNobodyIsRecordedWhereTheHostAllowsIt()
    {
        var (processor, store) = Disclosures.Audited(
            configure: _ =>
            {
                _.Caller = _ => null;
                _.AllowAnonymous = true;
            });

        await Disclosures.Buffered(processor, Disclosures.Names());

        var anonymous = await Disclosures.Events(store, caller: null);
        using (Assert.Multiple())
        {
            await Assert.That(anonymous.Single().Event.Caller).IsNull();
            await Assert.That(await Disclosures.Events(store)).IsEmpty();
        }
    }

    // The transport's word is taken over the resolver's: it is the one that authenticated somebody.
    [Test]
    public async Task TheTransportSaysWhoIsAsking()
    {
        var (processor, store) = Disclosures.Audited();

        await Disclosures.Buffered(processor, Disclosures.Names(), caller: "bob");

        using (Assert.Multiple())
        {
            await Assert.That(await Disclosures.Events(store, "bob")).Count().IsEqualTo(1);
            await Assert.That(await Disclosures.Events(store)).IsEmpty();
        }
    }

    [Test]
    public async Task TheAuditEntryNamesTheEvent()
    {
        var (processor, store) = Disclosures.Audited();
        var auditor = new RecordingAuditor();
        await using var services = Services(auditor);

        await Disclosures.Buffered(processor, Disclosures.Names(), services);
        Disclosures.Direct(processor, Disclosures.Names(), services);

        var events = await Disclosures.Events(store);
        await Assert.That(auditor.Entries.Select(_ => _.Disclosure)).IsEquivalentTo(
            events.Select(_ => (Guid?) _.Event.Id),
            CollectionOrdering.Matching);
    }

    // An auditor is allowed to throw, and one that does fails the request after its answer was put on
    // record. Nothing went, so the record is withdrawn rather than left saying something did.
    [Test]
    public async Task AnAnswerAnAuditorFailsIsWithdrawn()
    {
        var store = new ScryMemoryDisclosureStore();
        var tap = new TappedSink(store);
        var processor = Disclosures.Audited(tap);
        await using var services = Services(new ThrowingAuditor());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Disclosures.Buffered(processor, Disclosures.Names(), services));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => Disclosures.Direct(processor, Disclosures.Names(), services));

        var ids = tap.Batches.Select(_ => _.EventId).Distinct().ToList();
        await Assert.That(ids).Count().IsEqualTo(2);
        await Assert.That(await Disclosures.Events(store)).IsEmpty();
        foreach (var id in ids)
        {
            var withdrawn = (await store.Reconstruct(id))!;
            using (Assert.Multiple())
            {
                await Assert.That(withdrawn.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Retracted);
                await Assert.That(withdrawn.Close.Units).IsEqualTo(0);

                // What was accepted is still there to be read: the record of a withdrawal is that
                // something was ready to go and did not.
                await Assert.That(withdrawn.Units).Count().IsEqualTo(3);
            }
        }
    }

    [Test]
    public async Task AQueryThatIsRefusedOrFailsRecordsNothing()
    {
        var (processor, store) = Disclosures.Audited(_ => _.AddPolicy<Order, ThrowingPolicy>());

        await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => Disclosures.Buffered(processor, QueryRequest.Create("Missing", [new CountOp()])));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Disclosures.Buffered(processor, QueryRequest.Create("Order", [new CountOp()])));

        using (Assert.Multiple())
        {
            await Assert.That((await store.Status()).Events).IsEqualTo(0);
            await Assert.That(store.ContentCount).IsEqualTo(0);
        }
    }

    // A response too large to hold is sent as it is written. Every stretch that leaves has its rows on
    // record first: each time the body is written to, the rows it has been handed so far are checked
    // against what the sink had accepted by then.
    [Test]
    public async Task ASpilledAnswerLeavesOnlyWhatIsOnRecord()
    {
        var store = new ScryMemoryDisclosureStore();
        var accepted = 0;
        var tap = new TappedSink(store)
        {
            Accepting = _ => accepted += _.Units.Count
        };
        var processor = Disclosures.Audited(tap);
        var body = new WatchedBody(() => accepted);
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = body
            }
        };

        await using var data = TestContext.CreateSeeded();
        using var spill = new ResponseSpill(context, threshold: 48);
        await processor.TryExecuteBufferedAsync(
            Disclosures.Names(),
            data,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            new HeaderDictionary(),
            spill.Output,
            spill);
        await spill.CompleteAsync();

        var closed = (await Disclosures.Events(store)).Single().Close!;
        using (Assert.Multiple())
        {
            // More than one batch is what says the answer really was sent in stretches.
            await Assert.That(tap.Batches.Count).IsGreaterThan(1);
            await Assert.That(body.Ahead).IsEmpty();
            await Assert.That(body.Text).Contains("Carol");
            await Assert.That(closed.Outcome).IsEqualTo(ScryDisclosureOutcome.Released);
            await Assert.That(closed.Units).IsEqualTo(3);
        }
    }

    // The sink goes down part-way through a response that had begun to leave. Nothing more leaves: the
    // body holds exactly the rows the sink had accepted, and the event is left without a close, which
    // reads as "may have been sent" for those rows and for no others.
    [Test]
    public async Task ASpilledAnswerStopsWhereTheRecordDoes()
    {
        var store = new ScryMemoryDisclosureStore();
        var tap = new TappedSink(store);
        var processor = Disclosures.Audited(new RefusingSink(accepting: 1, inner: tap));
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = body
            }
        };

        await using var data = TestContext.CreateSeeded();
        using var spill = new ResponseSpill(context, threshold: 48);
        await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            async () => await processor.TryExecuteBufferedAsync(
                Disclosures.Names(),
                data,
                EmptyServiceProvider.Instance,
                new HeaderDictionary(),
                new HeaderDictionary(),
                spill.Output,
                spill));

        var sent = Encoding.UTF8.GetString(body.ToArray());
        var recorded = (await store.Reconstruct(tap.Batches.Single().EventId))!;
        var names = recorded.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).ToList();
        using (Assert.Multiple())
        {
            await Assert.That(recorded.Close).IsNull();
            await Assert.That(names).IsNotEmpty();
            await Assert.That(Regex.Matches(sent, "\\{\"name\":").Count).IsEqualTo(names.Count);
            foreach (var name in names)
            {
                await Assert.That(sent).Contains(name);
            }
        }
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

    sealed class ThrowingAuditor :
        IScryAuditor
    {
        public void Record(ScryAuditEntry entry) =>
            throw new InvalidOperationException("The auditor is down.");
    }

    sealed class ThrowingPolicy :
        IReturnablePolicy<Order>
    {
        public IQueryable<Order> Filter(IQueryable<Order> source, ScryPolicyContext context) =>
            throw new InvalidOperationException("The policy faulted.");
    }

    // A response body that, each time it is written to, counts the rows it has been handed so far and
    // notes any time that was more than the sink had accepted.
    sealed class WatchedBody(Func<int> accepted) :
        MemoryStream
    {
        public List<string> Ahead { get; } = [];

        public string Text => Encoding.UTF8.GetString(ToArray());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, Cancel cancel = default)
        {
            Write(buffer.Span);
            var sent = Regex.Matches(Text, "\\{\"name\":").Count;
            var held = accepted();
            if (sent > held)
            {
                Ahead.Add($"{sent} rows had left with {held} on record");
            }

            return ValueTask.CompletedTask;
        }
    }
}
