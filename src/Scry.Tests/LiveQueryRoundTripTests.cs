using Microsoft.EntityFrameworkCore;

/// <summary>
/// A live query from LINQ to rows and back again when the rows change, with the processor as the
/// client's transport and a real database under it. No web host: this is the seam a transport other
/// than HTTP plugs into, exercised from both ends at once.
/// </summary>
[TestFixture]
public class LiveQueryRoundTripTests
{
    [Test]
    public async Task RowsArriveAndArriveAgainWhenTheyChange()
    {
        await using var database = await Seeded("LiveRoundTripRows");
        var processor = Live();
        await using var reading = database.NewDbContext();
        var client = ClientFor(processor, reading);

        await using var answers = client
            .Source<Order>("Order")
            .OrderBy(_ => _.Region)
            .Select(_ => new {_.Region})
            .Live()
            // ReSharper disable once MethodSupportsCancellation
            .GetAsyncEnumerator();

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current.Select(_ => _.Region), Is.EqualTo(["North", "South"]));

        await Insert(database, processor, "West");

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current.Select(_ => _.Region), Is.EqualTo(["North", "South", "West"]));
    }

    [Test]
    public async Task ACountArrivesAndArrivesAgainWhenItChanges()
    {
        await using var database = await Seeded("LiveRoundTripCount");
        var processor = Live();
        await using var reading = database.NewDbContext();
        var client = ClientFor(processor, reading);

        await using var answers = client
            .Source<Order>("Order")
            .LiveCount(_ => _.Region == "North")
            // ReSharper disable once MethodSupportsCancellation
            .GetAsyncEnumerator();

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current, Is.EqualTo(1));

        await Insert(database, processor, "North");

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current, Is.EqualTo(2));
    }

    [Test]
    public async Task ACallbackIsHandedEachChange()
    {
        await using var database = await Seeded("LiveRoundTripCallback");
        var processor = Live();
        await using var reading = database.NewDbContext();
        var client = ClientFor(processor, reading);
        var seen = new Seen();

        await using var subscription = client
            .Source<Order>("Order")
            .LiveCount()
            .Subscribe(seen.Add);

        await seen.Reaches(1);
        await Insert(database, processor, "West");
        await seen.Reaches(2);

        Assert.That(seen.Values, Is.EqualTo([2, 3]));
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static ScryProcessor Live() =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.MaxSubscriptions = 10;
            options.SubscriptionThrottle = TimeSpan.Zero;
            options.SubscriptionPollInterval = null;
        });

    // begin-snippet: inProcessLiveClient
    static ScryClient ClientFor(ScryProcessor processor, TestContext context) =>
        new(
            (request, _) => Task.FromResult(processor.Execute(request, context)),
            subscribeTransport: (request, cancel) => processor.Subscribe(request, context, cancel));
    // end-snippet

    static Task<bool> Next<T>(IAsyncEnumerator<T> answers) =>
        answers.MoveNextAsync().AsTask().WaitAsync(patience);

    static async Task<SqlDatabase<TestContext>> Seeded(string name)
    {
        var database = await TestContext.CreateIsolated(name);
        await using var context = database.NewDbContext();
        context.Orders.AddRange(
            new()
            {
                Region = "North"
            },
            new()
            {
                Region = "South"
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
                Region = region
            });
        await writing.SaveChangesAsync();
    }

    sealed class Seen
    {
        List<int> values = [];

        public IReadOnlyList<int> Values
        {
            get
            {
                lock (values)
                {
                    return [.. values];
                }
            }
        }

        public void Add(int value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }

        public async Task Reaches(int count)
        {
            var started = Stopwatch.GetTimestamp();
            while (Values.Count < count)
            {
                Assert.That(Stopwatch.GetElapsedTime(started), Is.LessThan(patience), $"Expected {count} answers; saw {Values.Count}.");
                await Task.Delay(20);
            }
        }
    }
}
