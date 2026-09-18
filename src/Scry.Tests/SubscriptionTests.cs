using Microsoft.EntityFrameworkCore;

/// <summary>
/// A live query: the same request, answered again whenever the answer changes. What these pin is when
/// it runs and when it speaks — it must run for every change that could matter, and say nothing unless
/// what this caller may see is different from what it was last told.
/// </summary>
[TestFixture]
public class SubscriptionTests
{
    [Test]
    public async Task TheFirstAnswerIsTheQuerysOwn()
    {
        await using var database = await Seeded("SubscriptionFirst");
        await using var reading = database.NewDbContext();
        await using var answers = Live().Subscribe(Regions(), reading).GetAsyncEnumerator();

        Assert.That(await Next(answers), Is.EqualTo(["North", "South"]));
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

        Assert.That(await Next(answers), Is.EqualTo(["North", "South", "West"]));
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

        Assert.That(pending.IsCompleted, Is.False);

        // And it is still listening: a change that does show is sent.
        await Insert(database, processor, "North");
        Assert.That(await pending.WaitAsync(patience), Is.True);
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
        Assert.That(runs.Count, Is.EqualTo(1));

        await Insert(database, processor, "West");
        Assert.That(await pending.WaitAsync(patience), Is.True);
        Assert.That(runs.Count, Is.EqualTo(2));
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

        Assert.That(await answers.MoveNextAsync(), Is.True);
        Assert.That(answers.Current.Kind, Is.EqualTo(ResultKind.Scalar));
        Assert.That(answers.Current.Payload.GetInt32(), Is.EqualTo(2));

        await Insert(database, processor, "West");

        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        Assert.That(answers.Current.Payload.GetInt32(), Is.EqualTo(3));
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

        Assert.That(await Next(answers), Is.EqualTo(["North"]));

        processor.Changes.Attach(reading.Model);
        policy.Allowing = null;
        processor.PolicyCache.InvalidateScope<Order>(CountingRegionPolicy.Scope);

        Assert.That(await Next(answers), Is.EqualTo(["North", "South"]));
    }

    // A rejection is the request's, so it is thrown before anything is answered — where a transport
    // can still say so with a status.
    [Test]
    public async Task ARejectedRequestThrowsBeforeAnyAnswer()
    {
        await using var reading = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Nothing", []);
        await using var answers = Live().Subscribe(request, reading).GetAsyncEnumerator();

        Assert.ThrowsAsync<ScryValidationException>(async () => await answers.MoveNextAsync());
    }

    [Test]
    public async Task AnAnswerLargerThanALiveQueryMayHoldIsRejected()
    {
        await using var reading = TestContext.CreateSeeded();
        var processor = Live(_ => _.MaxSubscriptionBytes = 64);
        await using var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();

        var exception = Assert.ThrowsAsync<ScryValidationException>(async () => await answers.MoveNextAsync());
        Assert.That(exception!.Message, Does.Contain("64 bytes"));
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

        var exception = Assert.ThrowsAsync<ScrySubscriptionLimitException>(async () => await refused.MoveNextAsync());
        Assert.That(exception!.PerCaller, Is.False);
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
        var exception = Assert.ThrowsAsync<ScrySubscriptionLimitException>(async () => await refused.MoveNextAsync());
        Assert.That(exception!.PerCaller, Is.True);

        await using var third = TestContext.CreateSeeded();
        await using var allowed = Subscribe(processor, third, "bob").GetAsyncEnumerator();
        Assert.That(await allowed.MoveNextAsync(), Is.True);
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

        Assert.That(await again.MoveNextAsync(), Is.True);
    }

    // Off is the default, and off is absent rather than guarded.
    [Test]
    public async Task WithNoLimitSetThereAreNoLiveQueries()
    {
        await using var reading = TestContext.CreateSeeded();
        var processor = ScryProcessor.Create<TestContext>(options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
        await using var answers = processor.Subscribe(Regions(), reading).GetAsyncEnumerator();

        var exception = Assert.ThrowsAsync<Exception>(async () => await answers.MoveNextAsync());
        Assert.That(exception!.Message, Does.Contain(nameof(ScryOptions.MaxSubscriptions)));
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

        Assert.That(await pending.WaitAsync(patience), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(Read(answers.Current), Is.EqualTo(["Central", "East", "North", "South", "West"]));
            Assert.That(runs.Count, Is.EqualTo(2));
        });
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

        Assert.That(await Next(answers), Is.EqualTo(["East", "North", "South", "West"]));
        Assert.That(runs.Count, Is.EqualTo(2));
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

        Assert.That(await Next(answers), Is.EqualTo(["North", "South", "West"]));
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
            .Subscribe(Regions(), reading, Services(new RunCounter()))
            .GetAsyncEnumerator();
        await Next(answers);

        await InsertUnwatched(database, "West");
        token = "2";

        Assert.That(await Next(answers), Is.EqualTo(["North", "South", "West"]));
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

        Assert.That(runs.Count, Is.EqualTo(1));
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

        Assert.Multiple(() =>
        {
            Assert.That(runs.Entries, Has.Count.EqualTo(2));
            Assert.That(runs.Entries.Select(_ => _.Subscribed), Is.All.True);
            Assert.That(runs.Entries.Select(_ => _.Outcome), Is.All.EqualTo(ScryQueryOutcome.Success));
        });
    }

    // A query asked once is recorded as it always was.
    [Test]
    public void AQueryAskedOnceIsNotRecordedAsALiveQuerys()
    {
        using var reading = TestContext.CreateSeeded();
        var runs = new RunCounter();

        Live().Execute(Regions(), reading, Services(runs));

        Assert.That(runs.Entries.Single().Subscribed, Is.False);
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

            Assert.That(backplane.Subscriptions, Is.EqualTo(1));
        }

        await processor.Changes.Reconciled;
        Assert.That(backplane.Subscriptions, Is.Zero);
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
        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        return Read(answers.Current);
    }

    // An enumerator cannot be disposed while it is being asked for its next answer, so a test that
    // leaves one waiting ends it the way a caller does: by cancelling.
    static async Task End(CancelSource ending, Task<bool> pending)
    {
        await ending.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(() => pending);
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
        public object? GetService(Type serviceType) =>
            serviceType == typeof(CountingRegionPolicy) ? policy : null;
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
