using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
// These drive a live query's enumerator by hand and end it by disposing it, which is what the
// await using below is for — a live query runs "until cancel is cancelled or the enumeration is
// abandoned", and these abandon it. Where a token is wanted it goes to the call that opens the
// query, whose own parameter carries it into the iterator.
// ReSharper disable MethodSupportsCancellation

/// <summary>
/// A live query: the same request, answered again whenever the answer changes. What these pin is when
/// it runs and when it speaks — it must run for every change that could matter, and say nothing unless
/// what this caller may see is different from what it was last told.
/// </summary>
[NotInParallel]
public class SubscriptionTests
{
    [Test]
    public async Task TheFirstAnswerIsTheQuerysOwn()
    {
        await using var database = await Seeded("SubscriptionFirst");
        await using var reading = database.NewDbContext();
        await using var answers = Live().Subscribe(Regions(), reading).GetAsyncEnumerator();

        await Assert.That(await Next(answers)).IsEquivalentTo(["North", "South"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AWriteThatChangesTheAnswerSendsTheNewOne()
    {
        await using var database = await Seeded("SubscriptionPush");
        var processor = Live();
        await using var reading = database.NewDbContext();
        await using var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();
        await Next(answers);

        await Insert(database, processor, "West");

        await Assert.That(await Next(answers)).IsEquivalentTo(["North", "South", "West"], CollectionOrdering.Matching);
    }

    // The query ran — somebody wrote to what it reads — and found what this caller sees unchanged. So
    // when an answer arrives says nothing: a write the caller cannot see produces no answer at all.
    [Test]
    public async Task AWriteThatLeavesTheAnswerAloneSendsNothing()
    {
        await using var database = await Seeded("SubscriptionQuiet");
        var processor = Live();
        var runs = new RunCounter();
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions(where: "North"), reading, Services(runs))
            .GetAsyncEnumerator();
        await Next(answers);

        var pending = answers.MoveNextAsync().AsTask();
        await Insert(database, processor, "West");
        await runs.Reaches(2);

        await Assert.That(pending.IsCompleted).IsFalse();

        // And it is still listening: a change that does show is sent.
        await Insert(database, processor, "North");
        await Assert.That(await pending.WaitAsync(patience)).IsTrue();
    }

    [Test]
    public async Task AWriteToSomethingTheQueryDoesNotReadRunsNothing()
    {
        await using var database = await Seeded("SubscriptionUnrelated");
        var processor = Live();
        var runs = new RunCounter();
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions(), reading, Services(runs))
            .GetAsyncEnumerator();
        await Next(answers);
        var pending = answers.MoveNextAsync().AsTask();

        await using (var writing = Writing(database, processor))
        {
            writing.Add(
                new Department
                {
                    Name = "Legal"
                });
            await writing.SaveChangesAsync();
        }

        await Task.Delay(quiet);
        await Assert.That(runs.Count).IsEqualTo(1);

        await Insert(database, processor, "West");
        await Assert.That(await pending.WaitAsync(patience)).IsTrue();
        await Assert.That(runs.Count).IsEqualTo(2);
    }

    // Until the first run has said what it read, nothing is known about what matters — so a write that
    // lands while that run is in progress has to count, whatever it was to.
    [Test]
    public async Task AQueryOverRowsNothingCanWatchListensForEverything()
    {
        await using var database = await Seeded("SubscriptionPoco");
        var processor = Live();
        var runs = new RunCounter();
        await using var reading = database.NewDbContext();
        var request = Capture()
            .Source<Holiday>("Holiday")
            .Select(_ => new {_.Name})
            .ToScryRequest();
        using var ending = new CancelSource();
        await using var answers = processor
            .Subscribe(request, reading, Services(runs), ending.Token)
            .GetAsyncEnumerator();
        await answers.MoveNextAsync();
        var pending = answers.MoveNextAsync().AsTask();

        processor.Changes.Attach(reading.Model);
        processor.Changes.Notify<Department>();

        await runs.Reaches(2);
        await End(ending, pending);
    }

    [Test]
    public async Task ACountIsAsLiveAsAList()
    {
        await using var database = await Seeded("SubscriptionCount");
        var processor = Live();
        await using var reading = database.NewDbContext();
        var request = Capture().Source<Order>("Order").ToScryRequest(new CountOp());
        await using var answers = processor.Subscribe(request, reading).GetAsyncEnumerator();

        await Assert.That(await answers.MoveNextAsync()).IsTrue();
        await Assert.That(answers.Current.Kind).IsEqualTo(ResultKind.Scalar);
        await Assert.That(answers.Current.Payload.GetInt32()).IsEqualTo(2);

        await Insert(database, processor, "West");

        await Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience)).IsTrue();
        await Assert.That(answers.Current.Payload.GetInt32()).IsEqualTo(3);
    }

    // No row was written. Which rows the caller may see changed, and that is part of the answer.
    [Test]
    public async Task AChangedGrantSendsTheRowsItRevealed()
    {
        await using var database = await Seeded("SubscriptionGrant");
        var policy = new CountingRegionPolicy();
        var processor = Live(_ => _.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision));
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions(), reading, new OnlyPolicy(policy))
            .GetAsyncEnumerator();

        await Assert.That(await Next(answers)).IsEquivalentTo(["North"], CollectionOrdering.Matching);

        processor.Changes.Attach(reading.Model);
        policy.Allowing = null;
        processor.PolicyCache.InvalidateScope<Order>(CountingRegionPolicy.Scope);

        await Assert.That(await Next(answers)).IsEquivalentTo(["North", "South"], CollectionOrdering.Matching);
    }

    // A rejection is the request's, so it is thrown before anything is answered — where a transport
    // can still say so with a status.
    [Test]
    public async Task ARejectedRequestThrowsBeforeAnyAnswer()
    {
        await using var reading = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Nothing", []);
        await using var answers = Live().Subscribe(request, reading).GetAsyncEnumerator();

        await Assert.ThrowsExactlyAsync<ScryValidationException>(async () => await answers.MoveNextAsync());
    }

    [Test]
    public async Task AnAnswerLargerThanALiveQueryMayHoldIsRejected()
    {
        await using var reading = TestContext.CreateSeeded();
        var processor = Live(_ => _.MaxSubscriptionBytes = 64);
        await using var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(async () => await answers.MoveNextAsync());
        await Assert.That(exception!.Message).Contains("64 bytes");
    }

    [Test]
    public async Task OnePastTheServersLimitIsRefused()
    {
        await using var reading = TestContext.CreateSeeded();
        var processor = Live(_ => _.MaxSubscriptions = 1);
        await using var held = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();
        await held.MoveNextAsync();

        await using var another = TestContext.CreateSeeded();
        await using var refused = processor.Subscribe(Regions(), another).GetAsyncEnumerator();

        var exception = await Assert.ThrowsExactlyAsync<ScrySubscriptionLimitException>(async () => await refused.MoveNextAsync());
        await Assert.That(exception!.PerCaller).IsFalse();
    }

    [Test]
    public async Task OnePastACallersLimitIsRefusedForThatCallerOnly()
    {
        var processor = Live(_ => _.MaxSubscriptionsPerCaller = 1);
        await using var first = TestContext.CreateSeeded();
        await using var held = Subscribe(processor, first, "alice").GetAsyncEnumerator();
        await held.MoveNextAsync();

        await using var second = TestContext.CreateSeeded();
        await using var refused = Subscribe(processor, second, "alice").GetAsyncEnumerator();
        var exception = await Assert.ThrowsExactlyAsync<ScrySubscriptionLimitException>(async () => await refused.MoveNextAsync());
        await Assert.That(exception!.PerCaller).IsTrue();

        await using var third = TestContext.CreateSeeded();
        await using var allowed = Subscribe(processor, third, "bob").GetAsyncEnumerator();
        await Assert.That(await allowed.MoveNextAsync()).IsTrue();
    }

    [Test]
    public async Task ALiveQueryThatEndedGivesItsPlaceBack()
    {
        var processor = Live(_ => _.MaxSubscriptions = 1);
        await using (var first = TestContext.CreateSeeded())
        {
            await using var held = processor.Subscribe(Regions(), first).GetAsyncEnumerator();
            await held.MoveNextAsync();
        }

        await using var second = TestContext.CreateSeeded();
        await using var again = processor.Subscribe(Regions(), second).GetAsyncEnumerator();

        await Assert.That(await again.MoveNextAsync()).IsTrue();
    }

    // Off is the default, and off is absent rather than guarded.
    [Test]
    public async Task WithNoLimitSetThereAreNoLiveQueries()
    {
        await using var reading = TestContext.CreateSeeded();
        var processor = ScryProcessor.Create<TestContext>(options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
        await using var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();

        var exception = await Assert.ThrowsExactlyAsync<Exception>(async () => await answers.MoveNextAsync());
        await Assert.That(exception!.Message).Contains(nameof(ScryOptions.MaxSubscriptions));
    }

    // Changes inside the throttle are neither lost nor queued: one run answers for all of them.
    [Test]
    public async Task ChangesInsideTheThrottleAreAnsweredOnce()
    {
        await using var database = await Seeded("SubscriptionThrottle");
        var processor = Live(_ => _.SubscriptionThrottle = TimeSpan.FromMilliseconds(400));
        var runs = new RunCounter();
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions(), reading, Services(runs))
            .GetAsyncEnumerator();
        await Next(answers);
        var pending = answers.MoveNextAsync().AsTask();

        await Insert(database, processor, "East");
        await Insert(database, processor, "West");
        await Insert(database, processor, "Central");

        await Assert.That(await pending.WaitAsync(patience)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(Read(answers.Current)).IsEquivalentTo(["Central", "East", "North", "South", "West"], CollectionOrdering.Matching);
            await Assert.That(runs.Count).IsEqualTo(2);
        }
    }

    // Nothing runs while nobody is asking for the next answer, so a reader that falls behind is handed
    // the state of things when it comes back rather than a backlog of states that no longer hold.
    [Test]
    public async Task AReaderThatFellBehindGetsTheLatestAnswerOnly()
    {
        await using var database = await Seeded("SubscriptionConflate");
        var processor = Live();
        var runs = new RunCounter();
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions(), reading, Services(runs))
            .GetAsyncEnumerator();
        await Next(answers);

        await Insert(database, processor, "East");
        await Insert(database, processor, "West");

        await Assert.That(await Next(answers)).IsEquivalentTo(["East", "North", "South", "West"], CollectionOrdering.Matching);
        await Assert.That(runs.Count).IsEqualTo(2);
    }

    // Written through a context nothing is watching, as another system would: only the poll finds it.
    [Test]
    public async Task ThePollFindsWhatNothingReported()
    {
        await using var database = await Seeded("SubscriptionPoll");
        var processor = Live(_ => _.SubscriptionPollInterval = TimeSpan.FromMilliseconds(200));
        await using var reading = database.NewDbContext();
        await using var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();
        await Next(answers);

        await InsertUnwatched(database, "West");

        await Assert.That(await Next(answers)).IsEquivalentTo(["North", "South", "West"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AProbeThatMovedRunsTheQueryAgain()
    {
        await using var database = await Seeded("SubscriptionProbe");
        var token = "1";
        var processor = Live(options =>
        {
            // ReSharper disable once AccessToModifiedClosure
            options.ChangeProbe = (_, _) => new(token);
            options.ChangeProbeInterval = TimeSpan.FromMilliseconds(50);
        });
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions(), reading, Services(new()))
            .GetAsyncEnumerator();
        await Next(answers);

        await InsertUnwatched(database, "West");
        token = "2";

        await Assert.That(await Next(answers)).IsEquivalentTo(["North", "South", "West"], CollectionOrdering.Matching);
    }

    // A probe that fails every interval would otherwise be a query storm of its own making.
    [Test]
    public async Task AProbeThatThrowsRunsNothing()
    {
        await using var database = await Seeded("SubscriptionProbeFailing");
        var processor = Live(options =>
        {
            options.ChangeProbe = (_, _) => throw new("The database is away.");
            options.ChangeProbeInterval = TimeSpan.FromMilliseconds(20);
        });
        var runs = new RunCounter();
        await using var reading = database.NewDbContext();
        using var ending = new CancelSource();
        await using var answers = processor
            .Subscribe(Regions(), reading, Services(runs), ending.Token)
            .GetAsyncEnumerator();
        await Next(answers);
        var pending = answers.MoveNextAsync().AsTask();

        await Task.Delay(quiet);

        await Assert.That(runs.Count).IsEqualTo(1);
        await End(ending, pending);
    }

    [Test]
    public async Task EveryRunIsRecordedAsALiveQuerys()
    {
        await using var database = await Seeded("SubscriptionAudit");
        var processor = Live();
        var runs = new RunCounter();
        await using var reading = database.NewDbContext();
        await using var answers = processor
            .Subscribe(Regions(), reading, Services(runs))
            .GetAsyncEnumerator();
        await Next(answers);

        await Insert(database, processor, "West");
        await Next(answers);

        using (Assert.Multiple())
        {
            await Assert.That(runs.Entries).Count().IsEqualTo(2);
            await Assert.That(runs.Entries.Select(_ => _.Subscribed)).All(_ => Equals(_, true));
            await Assert.That(runs.Entries.Select(_ => _.Outcome)).All(_ => Equals(_, ScryQueryOutcome.Success));
        }
    }

    // A query asked once is recorded as it always was.
    [Test]
    public async Task AQueryAskedOnceIsNotRecordedAsALiveQuerys()
    {
        await using var reading = TestContext.CreateSeeded();
        var runs = new RunCounter();

        Live().Execute(Regions(), reading, Services(runs));

        await Assert.That(runs.Entries.Single().Subscribed).IsFalse();
    }

    // A node holds a place on the backplane for as long as it has something to re-ask, and no longer.
    [Test]
    public async Task ListeningLastsAsLongAsALiveQueryDoes()
    {
        var backplane = new CountingBackplane();
        var processor = Live();
        await using var services = new ServiceCollection()
            .AddSingleton<IScryChangeBackplane>(backplane)
            .BuildServiceProvider();

        await using (var reading = TestContext.CreateSeeded())
        {
            await using var answers = processor.Subscribe(Regions(), reading, services).GetAsyncEnumerator();
            await answers.MoveNextAsync();
            await processor.Changes.Reconciled;

            await Assert.That(backplane.Subscriptions).IsEqualTo(1);
        }

        await processor.Changes.Reconciled;
        await Assert.That(backplane.Subscriptions).IsZero();
    }

    // One write makes every live query due in the same instant. What the limit promises is that they
    // reach the database as a queue.
    [Test]
    public async Task NoMoreRunAtOnceThanTheServerAllows()
    {
        var most = await MostAtTheDatabase("SubscriptionQueue", allowed: 1);

        await Assert.That(most).IsEqualTo(1);
    }

    // The control for the test above: the same three, allowed to, do overlap — so a one there was the
    // limit's doing and not the way these happened to be scheduled.
    [Test]
    public async Task AsManyRunAtOnceAsTheServerAllows()
    {
        var most = await MostAtTheDatabase("SubscriptionStampede", allowed: 3);

        await Assert.That(most).IsGreaterThan(1);
    }

    static async Task<int> MostAtTheDatabase(string name, int allowed)
    {
        await using var database = await Seeded(name);
        var processor = Live(options => options.MaxConcurrentSubscriptionRuns = allowed);
        var gauge = new CommandGauge();
        List<TestContext> contexts = [];
        List<IAsyncEnumerator<QueryResponse>> held = [];
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var reading = new TestContext(
                    new DbContextOptionsBuilder<TestContext>()
                        .UseSqlServer(database.ConnectionString)
                        .AddInterceptors(gauge)
                        .Options);
                contexts.Add(reading);
                var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();
                held.Add(answers);
                await Next(answers);
            }

            gauge.Reset();
            await Insert(database, processor, "West");
            await Task.WhenAll(held.Select(Next));
            return gauge.Most;
        }
        finally
        {
            foreach (var answers in held)
            {
                await answers.DisposeAsync();
            }

            foreach (var reading in contexts)
            {
                await reading.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task TheLiveQueriesHeldOpenAreCounted()
    {
        List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = ListenMeters(measurements);
        await using var database = await Seeded("SubscriptionGauge");
        await using var reading = database.NewDbContext();

        await using (var answers = Live().Subscribe(Regions(), reading).GetAsyncEnumerator())
        {
            await Next(answers);

            await Assert.That(Active(measurements)).IsEquivalentTo([1L], CollectionOrdering.Matching);
        }

        await Assert.That(Active(measurements)).IsEquivalentTo([1L, -1L], CollectionOrdering.Matching);
    }

    // Refused, it never held a place, so it must not be counted as having taken or given one back.
    [Test]
    public async Task ARefusedLiveQueryIsNotCountedAsOpen()
    {
        List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements = [];
        await using var database = await Seeded("SubscriptionGaugeRefused");
        var processor = Live(options => options.MaxSubscriptions = 1);
        await using var reading = database.NewDbContext();
        await using var first = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();
        await Next(first);
        using var listener = ListenMeters(measurements);
        await using var refusedReading = database.NewDbContext();
        await using var refused = processor.Subscribe(Regions(), refusedReading).GetAsyncEnumerator();

        await Assert.ThrowsAsync<ScrySubscriptionLimitException>(async () => await refused.MoveNextAsync());

        await Assert.That(Active(measurements)).IsEmpty();
    }

    [Test]
    public async Task AProbeThatThrowsIsCounted()
    {
        List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = ListenMeters(measurements);
        await using var database = await Seeded("SubscriptionProbeCounted");
        var processor = Live(options =>
        {
            options.ChangeProbe = (_, _) => throw new InvalidOperationException("The database is away.");
            options.ChangeProbeInterval = TimeSpan.FromMilliseconds(20);
        });
        await using var services = Services(new());
        await using var reading = database.NewDbContext();
        using var ending = new CancelSource();
        await using var answers = processor
            .Subscribe(Regions(), reading, services, ending.Token)
            .GetAsyncEnumerator();
        await Next(answers);
        var pending = answers.MoveNextAsync().AsTask();

        var failure = await Eventually(
            () => Snapshot(measurements).FirstOrDefault(_ => _.Instrument == "scry.server.subscription.signal.failures"),
            _ => _.Instrument is not null);

        using (Assert.Multiple())
        {
            await Assert.That(failure.Value).IsEqualTo(1L);
            await Assert.That(failure.Tags["scry.signal"]).IsEqualTo("probe");
            await Assert.That(failure.Tags["error.type"]).IsEqualTo(typeof(InvalidOperationException).FullName);
        }
        await End(ending, pending);
    }

    // What separates the load callers asked for from the load other callers' writes caused.
    [Test]
    public async Task EveryRunIsTaggedAsALiveQuerys()
    {
        List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements = [];
        List<Activity> stopped = [];
        await using var database = await Seeded("SubscriptionTagged");
        var processor = Live();
        await using var reading = database.NewDbContext();
        using (ListenMeters(measurements))
        using (ListenActivities(stopped))
        {
            await using var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();
            await Next(answers);
            await Insert(database, processor, "West");
            await Next(answers);
        }

        var durations = Snapshot(measurements)
            .Where(_ => _.Instrument == "scry.server.query.duration")
            .ToList();
        using (Assert.Multiple())
        {
            await Assert.That(durations).Count().IsEqualTo(2);
            await Assert.That(durations.Select(_ => _.Tags.GetValueOrDefault("scry.subscription"))).All(_ => Equals(_, true));
            await Assert.That(stopped).Count().IsEqualTo(2);
            await Assert.That(stopped.Select(_ => _.GetTagItem("scry.subscription"))).All(_ => Equals(_, true));
        }
    }

    [Test]
    public async Task AQueryAskedOnceIsNotTaggedAsALiveQuerys()
    {
        List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements = [];
        List<Activity> stopped = [];
        await using var reading = TestContext.CreateSeeded();
        using (ListenMeters(measurements))
        using (ListenActivities(stopped))
        {
            Live().Execute(Regions(), reading);
        }

        using (Assert.Multiple())
        {
            await Assert.That(measurements.Single(_ => _.Instrument == "scry.server.query.duration").Tags).DoesNotContainKey("scry.subscription");
            await Assert.That(stopped.Single().GetTagItem("scry.subscription")).IsNull();
        }
    }

    // A limit that made no sense would otherwise surface as a live query that never ran, or ran without
    // pause — so it is refused where the mistake was made, at startup, naming the option.
    [Test]
    [MethodDataSource(nameof(OptionsOutOfRange))]
    public async Task AnOptionOutOfRangeIsRefusedAtStartup(string option, Action<ScryOptions> set)
    {
        var exception = Assert.Throws<Exception>(() => Live(set));

        await Assert.That(exception.Message).Contains($"ScryOptions.{option} ");
    }

    [Test]
    [MethodDataSource(nameof(OptionsAtTheirEdge))]
    public async Task AnOptionAtTheEdgeOfItsRangeIsAccepted(string option, Action<ScryOptions> set) =>
        await Assert.That(() => Live(set)).ThrowsNothing().Because(option);

    public static IEnumerable<TestDataRow<(string, Action<ScryOptions>)>> OptionsOutOfRange()
    {
        yield return Case(nameof(ScryOptions.MaxSubscriptions), _ => _.MaxSubscriptions = -1);
        yield return Case(nameof(ScryOptions.MaxSubscriptionsPerCaller), _ => _.MaxSubscriptionsPerCaller = 0);
        yield return Case(nameof(ScryOptions.MaxSubscriptionBytes), _ => _.MaxSubscriptionBytes = 0);
        yield return Case(nameof(ScryOptions.MaxConcurrentSubscriptionRuns), _ => _.MaxConcurrentSubscriptionRuns = 0);
        yield return Case(nameof(ScryOptions.SubscriptionThrottle), _ => _.SubscriptionThrottle = TimeSpan.FromTicks(-1));
        yield return Case(nameof(ScryOptions.SubscriptionPollInterval), _ => _.SubscriptionPollInterval = TimeSpan.Zero);
        yield return Case(nameof(ScryOptions.SubscriptionHeartbeat), _ => _.SubscriptionHeartbeat = TimeSpan.Zero);
        yield return Case(nameof(ScryOptions.SubscriptionLifetime), _ => _.SubscriptionLifetime = TimeSpan.Zero);
        yield return Case(nameof(ScryOptions.ChangeProbeInterval), _ => _.ChangeProbeInterval = TimeSpan.Zero);
    }

    public static IEnumerable<TestDataRow<(string, Action<ScryOptions>)>> OptionsAtTheirEdge()
    {
        yield return Case(nameof(ScryOptions.MaxSubscriptions), _ => _.MaxSubscriptions = 0);
        yield return Case(nameof(ScryOptions.MaxSubscriptionsPerCaller), _ => _.MaxSubscriptionsPerCaller = 1);
        yield return Case(nameof(ScryOptions.MaxSubscriptionBytes), _ => _.MaxSubscriptionBytes = 1);
        yield return Case(nameof(ScryOptions.MaxConcurrentSubscriptionRuns), _ => _.MaxConcurrentSubscriptionRuns = 1);
        yield return Case(nameof(ScryOptions.SubscriptionThrottle), _ => _.SubscriptionThrottle = TimeSpan.Zero);
        yield return Case(nameof(ScryOptions.SubscriptionPollInterval), _ => _.SubscriptionPollInterval = null);
        yield return Case(nameof(ScryOptions.SubscriptionLifetime), _ => _.SubscriptionLifetime = null);
    }

    static TestDataRow<(string, Action<ScryOptions>)> Case(string option, Action<ScryOptions> set) =>
        new((option, set), DisplayName: option);

    static List<long> Active(List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements) =>
    [
        .. Snapshot(measurements)
            .Where(_ => _.Instrument == "scry.server.subscriptions.active")
            .Select(_ => (long) _.Value)
    ];

    static List<(string Instrument, object Value, Dictionary<string, object?> Tags)> Snapshot(
        List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements)
    {
        lock (measurements)
        {
            return [.. measurements];
        }
    }

    static async Task<TValue> Eventually<TValue>(Func<TValue> read, Func<TValue, bool> arrived)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var value = read();
            if (arrived(value))
            {
                return value;
            }

            if (Stopwatch.GetElapsedTime(started) > patience)
            {
                Assert.Fail("What was waited for never arrived.");
            }

            await Task.Delay(20);
        }
    }

    static MeterListener ListenMeters(List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ScryInstrumentation.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(measurements, instrument, value, tags));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(measurements, instrument, value, tags));
        listener.Start();
        return listener;
    }

    static void Add(
        List<(string Instrument, object Value, Dictionary<string, object?> Tags)> measurements,
        Instrument instrument,
        object value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var read = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            read[tag.Key] = tag.Value;
        }

        lock (measurements)
        {
            measurements.Add((instrument.Name, value, read));
        }
    }

    static ActivityListener ListenActivities(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => _.Name == ScryInstrumentation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (stopped)
                {
                    stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// How many commands were at the database at once. Each is held there long enough that two live
    /// queries free to overlap would.
    /// </summary>
    sealed class CommandGauge :
        DbCommandInterceptor
    {
        static TimeSpan held = TimeSpan.FromMilliseconds(200);
        int current;
        int most;

        public int Most => Volatile.Read(ref most);

        public void Reset() =>
            Volatile.Write(ref most, 0);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            Cancel cancel = default)
        {
            Entered();
            await Task.Delay(held, cancel);
            return result;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Entered();
            Thread.Sleep(held);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            Cancel cancel = default)
        {
            Interlocked.Decrement(ref current);
            return new(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Interlocked.Decrement(ref current);
            return result;
        }

        void Entered()
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while (now > (seen = Volatile.Read(ref most)) &&
                   Interlocked.CompareExchange(ref most, now, seen) != seen)
            {
            }
        }
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);
    static TimeSpan quiet = TimeSpan.FromMilliseconds(300);

    // Reports and nothing else decide when these run: no throttle to wait out, no poll to race.
    static ScryProcessor Live(Action<ScryOptions>? extra = null) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.MaxSubscriptions = 10;
            options.SubscriptionThrottle = TimeSpan.Zero;
            options.SubscriptionPollInterval = null;
            extra?.Invoke(options);
        });

    static ScryClient Capture() =>
        new((_, _) => throw new("A captured request is never sent."));

    static QueryRequest Regions(string? where = null)
    {
        var orders = Capture().Source<Order>("Order");
        if (where is not null)
        {
            orders = orders.Where(_ => _.Region == where);
        }

        return orders
            .OrderBy(_ => _.Region)
            .Select(_ => new {_.Region})
            .ToScryRequest();
    }

    static IAsyncEnumerable<QueryResponse> Subscribe(ScryProcessor processor, TestContext reading, string caller) =>
        processor.Subscribe(
            Regions(),
            reading,
            EmptyServices.Instance,
            new HeaderDictionary(),
            new HeaderDictionary(),
            caller);

    static async Task<string[]> Next(IAsyncEnumerator<QueryResponse> answers)
    {
        await Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience)).IsTrue();
        return Read(answers.Current);
    }

    // An enumerator cannot be disposed while it is being asked for its next answer, so a test that
    // leaves one waiting ends it the way a caller does: by cancelling.
    static async Task End(CancelSource ending, Task<bool> pending)
    {
        await ending.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    static string[] Read(QueryResponse response) =>
    [
        .. response.Payload
            .EnumerateArray()
            .Select(_ => _.GetProperty("region").GetString()!)
    ];

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

    static TestContext Writing(SqlDatabase<TestContext> database, ScryProcessor processor) =>
        new(new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer(database.ConnectionString)
            .AddInterceptors(new ScryChangeInterceptor(processor.Changes))
            .Options);

    static async Task Insert(SqlDatabase<TestContext> database, ScryProcessor processor, string region)
    {
        await using var writing = Writing(database, processor);
        writing.Orders.Add(
            new()
            {
                Region = region,
                Revision = 10
            });
        await writing.SaveChangesAsync();
    }

    static async Task InsertUnwatched(SqlDatabase<TestContext> database, string region)
    {
        await using var writing = database.NewDbContext();
        writing.Orders.Add(
            new()
            {
                Region = region,
                Revision = 10
            });
        await writing.SaveChangesAsync();
    }

    static ServiceProvider Services(RunCounter runs) =>
        new ServiceCollection()
            .AddSingleton<IScryAuditor>(runs)
            .BuildServiceProvider();

    /// <summary>Counts runs by the audit entries they leave, which is one per run whether or not it spoke.</summary>
    sealed class RunCounter :
        IScryAuditor
    {
        List<ScryAuditEntry> entries = [];

        public int Count
        {
            get
            {
                lock (entries)
                {
                    return entries.Count;
                }
            }
        }

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
            while (Count < count)
            {
                if (Stopwatch.GetElapsedTime(started) > patience)
                {
                    Assert.Fail($"Expected {count} runs; saw {Count}.");
                }

                await Task.Delay(20);
            }
        }
    }

    sealed class OnlyPolicy(CountingRegionPolicy policy) :
        IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(CountingRegionPolicy))
            {
                return policy;
            }

            return null;
        }
    }

    sealed class EmptyServices :
        IServiceProvider
    {
        public static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) =>
            null;
    }

    sealed class CountingBackplane :
        IScryChangeBackplane
    {
        int subscriptions;

        public int Subscriptions => Volatile.Read(ref subscriptions);

        public ValueTask PublishAsync(ScryChange change, Cancel cancel) =>
            ValueTask.CompletedTask;

        public ValueTask<IAsyncDisposable> SubscribeAsync(Func<ScryChange, Cancel, ValueTask> handler, Cancel cancel)
        {
            Interlocked.Increment(ref subscriptions);
            return new(new Subscription(this));
        }

        sealed class Subscription(CountingBackplane owner) :
            IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref owner.subscriptions);
                return ValueTask.CompletedTask;
            }
        }
    }
}
