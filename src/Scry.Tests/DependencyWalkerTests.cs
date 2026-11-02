using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// What a live query listens for is what its last run read — read off the query as it actually ran,
/// so that a table only a policy names counts as much as one the client named. Too narrow and a change
/// is missed; "cannot tell" is always a safe answer, and is what null means here.
/// </summary>
public class DependencyWalkerTests
{
    [Test]
    public async Task AQueryReadsItsRoot()
    {
        var request = Capture()
            .Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToScryRequest();

        await Assert.That(await Read(request)).IsEquivalentTo(Names<Order>());
    }

    [Test]
    public async Task AReferenceNavigationReadsWhatItReaches()
    {
        var request = Capture()
            .Source<Employee>("Employee")
            .Select(_ => new {_.Name, Department = _.Department!.Name})
            .ToScryRequest();

        await Assert.That(await Read(request)).IsEquivalentTo(Names<Employee, Department>());
    }

    [Test]
    public async Task ANavigationInAPredicateCountsAsMuchAsOneInAProjection()
    {
        var request = Capture()
            .Source<Employee>("Employee")
            .Where(_ => _.Department!.Name == "Sales")
            .Select(_ => new {_.Name})
            .ToScryRequest();

        await Assert.That(await Read(request)).IsEquivalentTo(Names<Employee, Department>());
    }

    [Test]
    public async Task ACollectionNavigationReadsItsElements()
    {
        var request = Capture()
            .Source<Order>("Order")
            .Where(_ => _.Lines.Any())
            .Select(_ => new {_.Region})
            .ToScryRequest();

        await Assert.That(await Read(request)).IsEquivalentTo(Names<Order, OrderLine>());
    }

    // A derived type's rows are read through its root, and that is the name a write to either reports.
    [Test]
    public async Task ADerivedSourceReadsItsRoot()
    {
        var request = Capture()
            .Source<Vehicle>("Vehicle")
            .Select(_ => new {_.Name})
            .ToScryRequest();

        await Assert.That(await Read(request)).IsEquivalentTo(Names<Asset>());
    }

    // Whatever the terminal, the rows folded are the same rows.
    [Test]
    public async Task ATerminalReadsWhatItsQueryDoes()
    {
        var orders = Capture()
            .Source<Order>("Order")
            .Where(_ => _.Lines.Any());

        using (Assert.Multiple())
        {
            await Assert.That(await Read(orders.ToScryRequest(new CountOp()))).IsEquivalentTo(Names<Order, OrderLine>());
            await Assert.That(await Read(orders.ToScryRequest(new AnyOp(Predicate: null)))).IsEquivalentTo(Names<Order, OrderLine>());
            await Assert.That(await Read(orders.OrderBy(_ => _.Id).Select(_ => new {_.Id}).ToScryRequest(new FirstOp(OrDefault: true, Predicate: null)))).IsEquivalentTo(Names<Order, OrderLine>());
            await Assert.That(await Read(orders.OrderBy(_ => _.Id).Select(_ => new {_.Id}).ToScryRequest(new PageOp(Size: 1)))).IsEquivalentTo(Names<Order, OrderLine>());
        }
    }

    // A client generated against another model is answered through the general path, which carries
    // the enum aliases; what it read is read all the same, or the query would be asked again on every
    // change anywhere.
    [Test]
    public async Task ADriftedClientsQueryIsReadAllTheSame()
    {
        var request = Capture()
                          .Source<Order>("Order")
                          .Where(_ => _.Lines.Any())
                          .ToScryRequest(new CountOp()) with
                      {
                          Stamp = "stamp-from-an-older-model"
                      };

        await Assert.That(await Read(request)).IsEquivalentTo(Names<Order, OrderLine>());
    }

    // The client never named Department. The policy did, and a change there changes what this caller
    // may see — which is why the query is read as it ran rather than as it was asked.
    [Test]
    public async Task ATableOnlyAPolicyReadsIsReadAllTheSame()
    {
        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AddPolicy<Order, RegionsNamedAfterDepartmentsPolicy>();
        });
        var request = Capture()
            .Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToScryRequest();

        await Assert.That(await Read(request, processor)).IsEquivalentTo(Names<Order, Department>());
    }

    // Rows held in memory come from nowhere a write to the database could be seen to reach.
    [Test]
    public async Task APocoSourceCannotBeTold()
    {
        var request = Capture()
            .Source<Holiday>("Holiday")
            .Select(_ => new {_.Name})
            .ToScryRequest();

        await Assert.That(await Read(request)).IsNull();
    }

    // A global query filter is applied as EF compiles the query, after the expression the walker reads
    // was built, so it is read off the model instead. The shared model declares none; this one does, on
    // both sides of a navigation, so a walk that followed filters blindly would never end.
    [Test]
    public async Task AQueryFilterReadsWhatItReaches()
    {
        await using var context = Filtered();
        var model = context.Model;

        var names = DependencyWalker.Read(model, [((IQueryable) context.Set<Posting>()).Expression]);

        await Assert.That(names).IsEquivalentTo([Name<Posting>(model), Name<Journal>(model)]);
    }

    // The client never named Hold, and neither did the query: the filter did, by reading another of the
    // context's sets — the shape a tenant or a soft-delete filter takes.
    [Test]
    public async Task AQueryFilterReadingAnotherSetReadsIt()
    {
        await using var context = Filtered();
        var model = context.Model;

        var names = DependencyWalker.Read(model, [((IQueryable) context.Set<Account>()).Expression]);

        await Assert.That(names).IsEquivalentTo([Name<Account>(model), Name<Hold>(model)]);
    }

    static LedgerContext Filtered() =>
        new(new DbContextOptionsBuilder<LedgerContext>()
            .UseSqlServer("Server=(none)")
            .Options);

    static string Name<TEntity>(IModel model) =>
        model.FindEntityType(typeof(TEntity))!.Name;

    static ScryClient Capture() =>
        new((_, _) => throw new("A captured request is never sent."));

    static async Task<IReadOnlySet<string>?> Read(QueryRequest request, ScryProcessor? processor = null)
    {
        await using var context = TestContext.CreateSeeded();
        using var output = new PooledBufferWriter();
        var run = new SubscriptionRun();
        await (processor ?? SharedProcessor.Instance).TryExecuteBufferedAsync(
            request,
            context,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            new HeaderDictionary(),
            output,
            subscription: run);
        return run.Dependencies;
    }

    static string[] Names<TEntity>()
    {
        using var context = TestContext.CreateSeeded();
        return [context.Model.FindEntityType(typeof(TEntity))!.Name];
    }

    static string[] Names<TFirst, TSecond>() =>
        [.. Names<TFirst>(), .. Names<TSecond>()];

    // A posting is read only while its journal is open, a journal only while it has postings, and an
    // account only while nothing holds it. Never connected: the model is all that is read.
    sealed class LedgerContext(DbContextOptions<LedgerContext> options) :
        DbContext(options)
    {
        public DbSet<Hold> Holds => Set<Hold>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Posting>().HasQueryFilter(_ => !_.Journal.Closed);
            builder.Entity<Journal>().HasQueryFilter(_ => _.Postings.Count > 0);
            builder.Entity<Account>().HasQueryFilter(_ => !Holds.Any(hold => hold.AccountId == _.Id));
        }
    }

    sealed class Journal
    {
        public int Id { get; set; }
        public bool Closed { get; set; }
        public List<Posting> Postings { get; set; } = [];
    }

    sealed class Posting
    {
        public int Id { get; set; }
        public Journal Journal { get; set; } = null!;
    }

    sealed class Account
    {
        public int Id { get; set; }
    }

    sealed class Hold
    {
        public int Id { get; set; }
        public int AccountId { get; set; }
    }
}

/// <summary>
/// Never attached by default. Reads a table the client's query does not, the way a policy that checks
/// a grants table does.
/// </summary>
public sealed class RegionsNamedAfterDepartmentsPolicy :
    IReturnablePolicy<Order>
{
    public IQueryable<Order> Filter(IQueryable<Order> source, ScryPolicyContext context)
    {
        var departments = context.Db.Set<Department>();
        return source.Where(_ => !departments.Any(department => department.Name == _.Region));
    }
}
