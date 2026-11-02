/// <summary>
/// A row policy whose decision is too expensive to make in SQL. The answers are remembered and the
/// query carries a membership test over them; what the tests here pin is when a decision is actually
/// made — because a cache that decided too often would not be one, and one that decided too rarely
/// would hand over a row nobody had ruled on.
/// </summary>
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

        await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["North", "North"], CollectionOrdering.Matching);
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
        await Assert.That(policy.Decisions).IsEqualTo(3);

        await client.Source<Order>("Order").CountAsync();
        await Assert.That(policy.Decisions).IsEqualTo(3);
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
            await Assert.That(policy.Decisions).IsEqualTo(before + 1);
            await Assert.That(count).IsEqualTo(3);
        }
    }

    // A writer can take a revision and commit after another writer's higher one has been read. The
    // watermark waits before it passes a revision, so the row committed late is still read, and
    // decided, rather than taken as already answered.
    [Test]
    public async Task ARowCommittedBelowARevisionAlreadyReadIsStillDecided()
    {
        await using var database = await TestContext.CreateIsolated("CachedPolicyStraggler");
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        await Seed(database);

        await using (var seeded = database.NewDbContext())
        {
            var client = ClientFor(seeded, processor, policy);
            await client.Source<Order>("Order").CountAsync();
        }

        var before = policy.Decisions;
        await using (var writing = database.NewDbContext())
        {
            writing.Orders.Add(new()
            {
                Region = "North",
                Amount = 500m,
                Revision = 0,
                Code = "1",
                Audited = "true"
            });
            await writing.SaveChangesAsync();
        }

        await using (var reading = database.NewDbContext())
        {
            var client = ClientFor(reading, processor, policy);
            var count = await client.Source<Order>("Order").CountAsync();

            // The rows read before are read again, and not decided again: only the late one is.
            await Assert.That(policy.Decisions).IsEqualTo(before + 1);
            await Assert.That(count).IsEqualTo(3);
        }
    }

    // Once a version has settled the watermark passes it, and a row committed late below it is then
    // one the cache never reads: the window is the whole of the guarantee, which is why it is the
    // host's to set.
    [Test]
    public async Task ASettledRevisionIsPassed()
    {
        await using var database = await TestContext.CreateIsolated("CachedPolicySettled");
        var policy = new CountingRegionPolicy();
        var processor = Build(_ =>
        {
            _.CachedPolicySettle = TimeSpan.Zero;
            _.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision);
        });
        await Seed(database);

        await using (var seeded = database.NewDbContext())
        {
            await ClientFor(seeded, processor, policy).Source<Order>("Order").CountAsync();
        }

        await using (var writing = database.NewDbContext())
        {
            writing.Orders.Add(new()
            {
                Region = "North",
                Amount = 500m,
                Revision = 0,
                Code = "1",
                Audited = "true"
            });
            await writing.SaveChangesAsync();
        }

        await using var reading = database.NewDbContext();
        var count = await ClientFor(reading, processor, policy).Source<Order>("Order").CountAsync();

        await Assert.That(count).IsEqualTo(2);
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

            await Assert.That(policy.Decisions).IsEqualTo(before + 1);
            await Assert.That(count).IsEqualTo(3);
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
            await Assert.That(policy.Decisions).IsEqualTo(before + 1);
            await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["North", "North", "South"], CollectionOrdering.Matching);
        }
    }

    // A key of another type than the key member's is refused at the call. Pended, it would be cast to
    // the key's type by every refresh of every scope, and each would fail before deciding anything.
    [Test]
    public async Task AKeyOfAnotherTypeIsRefusedAtTheCall()
    {
        await using var database = await TestContext.CreateIsolated("CachedPolicyMistypedKey");
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        await Seed(database);
        await using var reading = database.NewDbContext();
        var client = ClientFor(reading, processor, policy);
        var before = await client.Source<Order>("Order").CountAsync();

        var exception = Assert.Throws<ArgumentException>(() => processor.PolicyCache.InvalidateRows<Order>([5L]));

        using (Assert.Multiple())
        {
            await Assert.That(exception!.Message).Contains("Int64");
            await Assert.That(await client.Source<Order>("Order").CountAsync()).IsEqualTo(before);
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

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["North", "North"], CollectionOrdering.Matching);
            // Three rows, then the one the host re-pended, decided once more.
            await Assert.That(policy.Decisions).IsEqualTo(4);
        }
    }

    [Test]
    public async Task ForgettingAScopeDecidesEveryRowAgain()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        var client = ClientFor(context, processor, policy);

        await client.Source<Order>("Order").CountAsync();
        await Assert.That(policy.Decisions).IsEqualTo(3);

        processor.PolicyCache.InvalidateScope<Order>(CountingRegionPolicy.Scope);

        await client.Source<Order>("Order").CountAsync();
        await Assert.That(policy.Decisions).IsEqualTo(6);
    }

    // Between a forgetting and the next read, the host can say more: pend a row, or prime one. The
    // scope read then is not empty, but every row in it was still forgotten, and the rows this
    // adapter remembers deciding above the watermark were forgotten with them. Skipping those as
    // already decided would leave them out of a set that no longer holds them — the caller sees
    // only the row the host pended, and once the watermark passes the others they are never read
    // again.
    [Test]
    public async Task ForgettingAScopeAndThenPendingARowDecidesEveryRowAgain()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        var client = ClientFor(context, processor, policy);

        await client.Source<Order>("Order").CountAsync();
        await Assert.That(policy.Decisions).IsEqualTo(3);

        var southId = context.Orders.Single(_ => _.Region == "South").Id;
        processor.PolicyCache.InvalidateScope<Order>(CountingRegionPolicy.Scope);
        processor.PolicyCache.InvalidateRows<Order>([southId]);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(policy.Decisions).IsEqualTo(6);
            await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["North", "North"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ForgettingAScopeAndThenPrimingARowDecidesEveryRowAgain()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var processor = Cached();
        var client = ClientFor(context, processor, policy);

        await client.Source<Order>("Order").CountAsync();
        await Assert.That(policy.Decisions).IsEqualTo(3);

        // One allowed row primed into the forgotten scope, so the scope read next has a key in it.
        // The other north row is owed a decision as much as it was before the priming.
        var primed = context.Orders.First(_ => _.Region == "North");
        processor.PolicyCache.InvalidateScope<Order>(CountingRegionPolicy.Scope);
        processor.PolicyCache.Prime(CountingRegionPolicy.Scope, [primed], Context(context, policy));
        await Assert.That(policy.Decisions).IsEqualTo(4);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(policy.Decisions).IsEqualTo(7);
            await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["North", "North"], CollectionOrdering.Matching);
        }
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
        await Assert.That(northRows.Select(_ => _.Region)).IsEquivalentTo(["North", "North"], CollectionOrdering.Matching);
        await Assert.That(southRows.Select(_ => _.Region)).IsEquivalentTo(["South"], CollectionOrdering.Matching);
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
        await Assert.That(policy.Decisions).IsEqualTo(3);

        var rows = await ClientFor(context, processor, policy).Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Region)).IsEquivalentTo(["North", "North"], CollectionOrdering.Matching);
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

        await Assert.That(sql).Contains("SELECT");
        await Assert.That(policy.Decisions).IsZero();
    }

    [Test]
    public async Task ATooLargeAllowedSetIsRefusedRatherThanSentToTheDatabase()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy {Allowing = null};
        var processor = Build(_ =>
        {
            _.MaxCachedPolicyKeys = 1;
            _.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision);
        });

        // Allowing everything is what a policy written as a cache but behaving like none looks like,
        // and every allowed key travels with every query.
        var exception = (await Assert.ThrowsExactlyAsync<Exception>(
            () => ClientFor(context, processor, policy).Source<Order>("Order").CountAsync()))!;

        await Assert.That(exception.Message).Contains("MaxCachedPolicyKeys");
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

        await Assert.That(policy.Decisions).IsEqualTo(6);
    }

    // The bound on the work rather than on the result. A scope nothing has been decided for reads
    // every row of the table, per scope key, so a table past the bound is refused from a count —
    // before a row is read, which is what the policy never being asked proves.
    [Test]
    public async Task ATooLargeColdScopeIsRefusedBeforeItsRowsAreRead()
    {
        await using var context = TestContext.CreateSeeded();
        var policy = new CountingRegionPolicy();
        var processor = Build(_ =>
        {
            _.MaxCachedPolicyRows = 2;
            _.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision);
        });

        var exception = (await Assert.ThrowsExactlyAsync<Exception>(
            () => ClientFor(context, processor, policy).Source<Order>("Order").CountAsync()))!;

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("MaxCachedPolicyRows");
            await Assert.That(policy.Decisions).IsZero();
        }
    }

    [Test]
    public async Task ATypeWithNoKeyIsRefusedAtStartup()
    {
        // Answers are filed per row by one key value, so a type without one has nowhere to put them.
        var exception = Assert.ThrowsExactly<Exception>(
            () => Build(_ => _.AddCachedPolicy<Holiday, Date, HolidayPolicy>(holiday => holiday.Date)));

        await Assert.That(exception.Message).Contains("key");
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
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(CountingRegionPolicy))
            {
                return policy;
            }

            return null;
        }
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

/// <summary>
/// The registrations <c>Schema.Build</c>, and the startup check against the real model, refuse — each
/// over a model compiled for it (<see cref="CompiledModel"/>), since a shape that refuses to start
/// cannot live in this assembly's model.
/// </summary>
public class CachedPolicyRegistrationTests
{
    // Answers are filed in the store under the policy type's name, so one type registered against two
    // entities — legal, the interface being contravariant — would file both under one record, with one
    // entity's allowed keys and watermark standing for the other's.
    [Test]
    public async Task OnePolicyTypeCannotServeTwoEntities()
    {
        var (assembly, _) = CompiledModel.Compile(
            """
            public sealed class Context : DbContext;

            [Queryable]
            public class Asset
            {
                public int Id { get; set; }
                public long Revision { get; set; }
            }

            [Queryable]
            public class Vehicle : Asset
            {
                public int Wheels { get; set; }
            }

            public class RegionPolicy :
                ICachedRowPolicy<Asset>
            {
                public string ScopeKey(ScryPolicyContext context) => "";

                public bool Allow(Asset row, string scopeKey, ScryPolicyContext context) => true;
            }
            """);
        var options = new ScryOptions(assembly.GetType("Context")!);
        Register(options, assembly.GetType("Asset")!, assembly.GetType("RegionPolicy")!);
        Register(options, assembly.GetType("Vehicle")!, assembly.GetType("RegionPolicy")!);

        var exception = Assert.ThrowsExactly<Exception>(() => Schema.Build(options));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("Cached row policy 'RegionPolicy' is registered against both 'Asset' and 'Vehicle'");
            await Assert.That(exception.Message).Contains("sealed class VehicleRegionPolicy : RegionPolicy");
        }
    }

    // Answers are filed per row under a primary key, and the rows owed one are read back from the
    // context's set: a POCO has no table for that, and a keyless view no key. Refused at startup rather
    // than faulting on the first query to read the source.
    [Test]
    [Arguments("[QueryablePoco]", "poco")]
    [Arguments("[Queryable] [Keyless]", "view")]
    public async Task ASourceWithNoTableOrNoKeyIsRefused(string optIn, string kind)
    {
        var (assembly, _) = CompiledModel.Compile(
            $$"""
              public sealed class Context : DbContext;

              {{optIn}}
              public class Closure
              {
                  public int Id { get; set; }
                  public long Revision { get; set; }
              }

              public sealed class ClosurePolicy :
                  ICachedRowPolicy<Closure>
              {
                  public string ScopeKey(ScryPolicyContext context) => "";

                  public bool Allow(Closure row, string scopeKey, ScryPolicyContext context) => true;
              }
              """);
        var options = new ScryOptions(assembly.GetType("Context")!);
        Register(options, assembly.GetType("Closure")!, assembly.GetType("ClosurePolicy")!);

        var exception = Assert.ThrowsExactly<Exception>(() => Schema.Build(options));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains($"Cached row policy 'ClosurePolicy' is registered against 'Closure', a {kind} source");
            await Assert.That(exception.Message).Contains("Attach it to a [Queryable] entity");
        }
    }

    // The key is derived from [Key] and the naming conventions, as a client's is, so one the context
    // configures fluently is not the one the answers would be filed under. Refused where the real model
    // is first in hand, naming the member to mark.
    [Test]
    public async Task AKeyTheContextConfiguresDifferentlyIsRefused()
    {
        var (assembly, _) = CompiledModel.Compile(
            """
            public sealed class Context(DbContextOptions options) :
                DbContext(options)
            {
                public DbSet<Order> Orders => Set<Order>();

                protected override void OnModelCreating(ModelBuilder builder) =>
                    builder.Entity<Order>().HasKey(_ => _.Code);
            }

            [Queryable]
            public class Order
            {
                public int Id { get; set; }
                public int Code { get; set; }
                public long Revision { get; set; }
            }

            public sealed class OrderPolicy :
                ICachedRowPolicy<Order>
            {
                public string ScopeKey(ScryPolicyContext context) => "";

                public bool Allow(Order row, string scopeKey, ScryPolicyContext context) => true;
            }
            """);
        var contextType = assembly.GetType("Context")!;
        var options = new ScryOptions(contextType);
        Register(options, assembly.GetType("Order")!, assembly.GetType("OrderPolicy")!);
        var schema = Schema.Build(options);

        // Spelled out rather than imported: EF's query extensions share names with Scry's. No server is
        // reached — the model is built from the provider alone.
        var contextOptions = Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions
            .UseSqlServer(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder(), "Server=(none)")
            .Options;
        await using var context = (Microsoft.EntityFrameworkCore.DbContext) Activator.CreateInstance(contextType, contextOptions)!;

        var exception = Assert.ThrowsExactly<Exception>(() => schema.ValidateAgainstModel(context.Model, contextType));

        await Assert.That(exception.Message).Contains("Cached row policy 'OrderPolicy' files its answers for 'Order' under a key derived as 'Id', but Context gives 'Order' a primary key of (Code)");
    }

    // What AddCachedPolicy<TEntity, TVersion, TPolicy> records, for types that exist only once compiled.
    static void Register(ScryOptions options, Type entity, Type policy)
    {
        var parameter = Expression.Parameter(entity, "_");
        var version = Expression.Lambda(Expression.Property(parameter, "Revision"), parameter);
        options.CachedPolicies[entity] = (policy, version, DeniedRowHandling.Default);
    }
}
