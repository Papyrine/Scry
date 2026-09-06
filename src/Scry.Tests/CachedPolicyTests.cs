/// <summary>
/// A row policy whose decision is too expensive to make in SQL. The answers are remembered and the
/// query carries a membership test over them; what the tests here pin is when a decision is actually
/// made — because a cache that decided too often would not be one, and one that decided too rarely
/// would hand over a row nobody had ruled on.
/// </summary>
[TestFixture]
public class CachedPolicyTests
{
    [Test]
    public async Task TheQueryCarriesWhatWasDecidedRatherThanTheDecision()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var client = ClientFor(context, Cached(), policy);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Amount)
            .Select(_ => new {_.Region})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Region), Is.EqualTo(["North", "North"]));
    }

    [Test]
    public async Task EveryRowIsDecidedOnceAndThenNotAgain()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var client = ClientFor(context, Cached(), policy);

        // The first query has nothing to go on and decides all three orders. The second finds every
        // answer already made — which is the whole point, and what a policy too expensive for SQL
        // needs to be true.
        await client.Source<Order>("Order").CountAsync();
        Assert.That(policy.Decisions, Is.EqualTo(3));

        await client.Source<Order>("Order").CountAsync();
        Assert.That(policy.Decisions, Is.EqualTo(3));
    }

    [Test]
    public async Task ANewRowIsDecidedOnItsFirstRead()
    {
        await using var database = await TestContext.CreateIsolated("CachedPolicyInsert");
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        await Seed(database);

        await using (var seeded = database.NewDbContext())
        {
            var client = ClientFor(seeded, processor, policy);
            await client.Source<Order>("Order").CountAsync();
        }

        var before = policy.Decisions;

        // Inserted behind the server's back, as any other writer would. Its revision is past the
        // watermark, so the next read finds it undecided rather than assuming either answer.
        await using (var writing = database.NewDbContext())
        {
            writing.Orders.Add(new()
            {
                Region = "North",
                Amount = 500m,
                Revision = 99,
                Code = "1",
                Audited = "true"
            });
            await writing.SaveChangesAsync();
        }

        await using (var reading = database.NewDbContext())
        {
            var client = ClientFor(reading, processor, policy);
            var count = await client.Source<Order>("Order").CountAsync();

            // Decided exactly once, and allowed: the row is in the result on the same read that
            // decided it, not the one after.
            Assert.That(policy.Decisions, Is.EqualTo(before + 1));
            Assert.That(count, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task AChangedRowIsDecidedAgainAndNothingElseIs()
    {
        await using var database = await TestContext.CreateIsolated("CachedPolicyUpdate");
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        await Seed(database);

        await using (var seeded = database.NewDbContext())
        {
            var client = ClientFor(seeded, processor, policy);
            await client.Source<Order>("Order").CountAsync();
        }

        var before = policy.Decisions;

        // The south order moves north and says so by moving its revision past the watermark.
        await using (var writing = database.NewDbContext())
        {
            var order = writing.Orders.Single(_ => _.Region == "South");
            order.Region = "North";
            order.Revision = 50;
            await writing.SaveChangesAsync();
        }

        await using (var reading = database.NewDbContext())
        {
            var client = ClientFor(reading, processor, policy);
            var count = await client.Source<Order>("Order").CountAsync();

            Assert.That(policy.Decisions, Is.EqualTo(before + 1));
            Assert.That(count, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task ARowWhoseGrantChangedIsDecidedAgainOnceTheHostSaysSo()
    {
        await using var database = await TestContext.CreateIsolated("CachedPolicyInvalidateRows");
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        await Seed(database);

        int southId;
        await using (var seeded = database.NewDbContext())
        {
            var client = ClientFor(seeded, processor, policy);
            await client.Source<Order>("Order").CountAsync();
            southId = seeded.Orders.Single(_ => _.Region == "South").Id;
        }

        var before = policy.Decisions;

        // Nothing about the row changed, so its revision has not moved and nothing else could know the
        // answer is stale. The host is what knows.
        policy.Allowing = "South";
        processor.PolicyCache.InvalidateRows<Order>([southId]);

        await using (var reading = database.NewDbContext())
        {
            var client = ClientFor(reading, processor, policy);
            var rows = await client.Source<Order>("Order")
                .OrderBy(_ => _.Region)
                .Select(_ => new {_.Region})
                .ToListAsync();

            // Exactly one row was decided again, and the answer moved with the grant. The others keep
            // the answers already made for them — invalidating a row is not invalidating the scope,
            // which is the difference the two methods exist to draw.
            Assert.That(policy.Decisions, Is.EqualTo(before + 1));
            Assert.That(rows.Select(_ => _.Region), Is.EqualTo(["North", "North", "South"]));
        }
    }

    [Test]
    public async Task AGrantRevokedWhileARowIsBeingDecidedDoesNotStand()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy
        {
            Allowing = null
        };
        var processor = Cached();
        var client = ClientFor(context, processor, policy);

        // The round decides the South row under the grant of the moment — allowed — and, before the
        // round is applied, the host revokes South and says so. The stale answer must not be the one
        // the query is served: the row is decided again, under the grant that now holds.
        policy.Decided = row =>
        {
            if (row.Region != "South")
            {
                return;
            }

            policy.Decided = null;
            policy.Allowing = "North";
            processor.PolicyCache.InvalidateRows<Order>([row.Id]);
        };

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Region)
            .Select(_ => new {_.Region})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(_ => _.Region), Is.EqualTo(["North", "North"]));
            // Three rows, then the one the host re-pended, decided once more.
            Assert.That(policy.Decisions, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task ForgettingAScopeDecidesEveryRowAgain()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        var client = ClientFor(context, processor, policy);

        await client.Source<Order>("Order").CountAsync();
        Assert.That(policy.Decisions, Is.EqualTo(3));

        processor.PolicyCache.InvalidateScope<Order>(CountingRegionPolicy.Scope);

        await client.Source<Order>("Order").CountAsync();
        Assert.That(policy.Decisions, Is.EqualTo(6));
    }

    [Test]
    public async Task AnswersAreKeptApartByScope()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = Cached();

        var north = new CountingRegionPolicy {Scoped = "north"};
        var south = new CountingRegionPolicy {Scoped = "south", Allowing = "South"};

        var northRows = await ClientFor(context, processor, north).Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToListAsync();
        var southRows = await ClientFor(context, processor, south).Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToListAsync();

        // Two callers, two sets of answers. One scope's decisions must never be the other's, which is
        // the one thing a shared cache has to get right.
        Assert.That(northRows.Select(_ => _.Region), Is.EqualTo(["North", "North"]));
        Assert.That(southRows.Select(_ => _.Region), Is.EqualTo(["South"]));
    }

    [Test]
    public async Task PrimingMeansTheFirstReadDecidesNothing()
    {
        await using var database = await TestContext.CreateIsolated("CachedPolicyPrime");
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        await Seed(database);

        await using var context = database.NewDbContext();
        var orders = context.Orders.ToList();

        // What a host does just after writing rows: decide them while it still has them in hand, so
        // the cost does not land on whoever queries next.
        processor.PolicyCache.Prime(CountingRegionPolicy.Scope, orders, Context(context, policy));
        Assert.That(policy.Decisions, Is.EqualTo(3));

        var rows = await ClientFor(context, processor, policy).Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Region), Is.EqualTo(["North", "North"]));
    }

    [Test]
    public async Task ShowingTheSqlDecidesNothing()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))]))]);

        // A preview runs no query, so nobody is reading rows and nothing is owed an answer. The filter
        // is still in the SQL — over the empty set of keys nothing has been decided into.
        var sql = Cached().ToQueryString(request, context, new StubProvider(policy));

        Assert.That(sql, Does.Contain("SELECT"));
        Assert.That(policy.Decisions, Is.Zero);
    }

    [Test]
    public void ATooLargeAllowedSetIsRefusedRatherThanSentToTheDatabase()
    {
        using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy {Allowing = null};
        var processor = Build(_ =>
        {
            _.MaxCachedPolicyKeys = 1;
            _.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision);
        });

        // Allowing everything is what a policy written as a cache but behaving like none looks like,
        // and every allowed key travels with every query.
        var exception = Assert.ThrowsAsync<Exception>(
            () => ClientFor(context, processor, policy).Source<Order>("Order").CountAsync())!;

        Assert.That(exception.Message, Does.Contain("MaxCachedPolicyKeys"));
    }

    // The hazard the doc names, given a test: a scope key read from a request header is a scope key
    // the caller chooses. Two callers with different headers share nothing, and every row is decided
    // again for the second — which is what proves the caller, not the server, picked the scope.
    [Test]
    public async Task AScopeKeyReadFromAHeaderIsChosenByTheCaller()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new HeaderScopedPolicy();
        var processor = Build(_ => _.AddCachedPolicy<Order, long, HeaderScopedPolicy>(order => order.Revision));
        var request = QueryRequest.Create("Order", [new CountOp()]);

        var services = new ServiceCollection().AddSingleton(policy).BuildServiceProvider();

        processor.Execute(request, context, services, new HeaderDictionary {["X-Scope"] = "one"}, new HeaderDictionary());
        processor.Execute(request, context, services, new HeaderDictionary {["X-Scope"] = "two"}, new HeaderDictionary());

        Assert.That(policy.Decisions, Is.EqualTo(6));
    }

    // The bound on the work rather than on the result. A scope nothing has been decided for reads
    // every row of the table, per scope key, so a table past the bound is refused from a count —
    // before a row is read, which is what the policy never being asked proves.
    [Test]
    public void ATooLargeColdScopeIsRefusedBeforeItsRowsAreRead()
    {
        using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var processor = Build(_ =>
        {
            _.MaxCachedPolicyRows = 2;
            _.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision);
        });

        var exception = Assert.ThrowsAsync<Exception>(
            () => ClientFor(context, processor, policy).Source<Order>("Order").CountAsync())!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain("MaxCachedPolicyRows"));
            Assert.That(policy.Decisions, Is.Zero);
        });
    }

    [Test]
    public void ATypeWithNoKeyIsRefusedAtStartup()
    {
        // Answers are filed per row by one key value, so a type without one has nowhere to put them.
        var exception = Assert.Throws<Exception>(
            () => Build(_ => _.AddCachedPolicy<Holiday, Date, HolidayPolicy>(holiday => holiday.Date)))!;

        Assert.That(exception.Message, Does.Contain("key"));
    }

    /// <summary>
    /// Three orders of its own. A database that has to be written to cannot be the one every other
    /// test in this assembly reads, and what these tests need of it is only regions and revisions.
    /// </summary>
    static async Task Seed(SqlDatabase<TestContext> database)
    {
        await using var context = database.NewDbContext();
        context.Orders.AddRange(
            new()
            {
                Region = "North",
                Revision = 1
            },
            new()
            {
                Region = "North",
                Revision = 2
            },
            new()
            {
                Region = "South",
                Revision = 3
            });

        await context.SaveChangesAsync();
    }

    static ScryProcessor Cached() =>
        Build(_ => _.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision));

    static ScryProcessor Build(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });

    static ScryPolicyContext Context(TestContext data, CountingRegionPolicy policy) =>
        new(new StubProvider(policy), data);

    static ScryClient ClientFor(TestContext context, ScryProcessor processor, CountingRegionPolicy policy) =>
        new((request, _) => Task.FromResult(
            processor.Execute(request, context, new StubProvider(policy), new HeaderDictionary(), new HeaderDictionary())));

    /// <summary>Hands back the one policy instance a test is counting, in place of a container.</summary>
    sealed class StubProvider(CountingRegionPolicy policy) :
        IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(CountingRegionPolicy) ? policy : null;
    }
}

/// <summary>
/// Stands in for a decision too expensive to make in SQL by counting how often it is made. What it
/// answers is trivial; when it is asked is the whole subject.
/// </summary>
/// <summary>
/// Never registered by default. The scope key the documentation warns against: one the caller
/// chooses, by sending a header.
/// </summary>
public sealed class HeaderScopedPolicy :
    ICachedRowPolicy<Order>
{
    public int Decisions { get; private set; }

    public string ScopeKey(ScryPolicyContext context) =>
        context.RequestHeaders["X-Scope"].ToString();

    public bool Allow(Order row, string scopeKey, ScryPolicyContext context)
    {
        Decisions++;
        return true;
    }
}

public sealed class CountingRegionPolicy :
    ICachedRowPolicy<Order>
{
    public string ScopeKey(ScryPolicyContext context) => Scoped;

    public bool Allow(Order row, string scopeKey, ScryPolicyContext context)
    {
        Decisions++;
        var allowed = Allowing is null || row.Region == Allowing;

        // After the answer is made and before it is handed back — the window in which a host that
        // changes a grant and says so is racing the round that is deciding under the old one.
        Decided?.Invoke(row);
        return allowed;
    }

    /// <summary>What a test wants to happen while a decision is being made, once the answer is fixed.</summary>
    public Action<Order>? Decided { get; set; }

    public const string Scope = "scope";

    /// <summary>The region allowed, or null to allow everything.</summary>
    public string? Allowing { get; set; } = "North";

    public string Scoped { get; set; } = Scope;

    /// <summary>How many rows this policy has been asked about.</summary>
    public int Decisions { get; private set; }
}

/// <summary>Never registered by a passing test: a POCO source has no key to remember answers by.</summary>
public sealed class HolidayPolicy :
    ICachedRowPolicy<Holiday>
{
    public string ScopeKey(ScryPolicyContext context) => "";

    public bool Allow(Holiday row, string scopeKey, ScryPolicyContext context) => true;
}
