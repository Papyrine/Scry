/// <summary>
/// What a live query listens for is what its last run read — read off the query as it actually ran,
/// so that a table only a policy names counts as much as one the client named. Too narrow and a change
/// is missed; "cannot tell" is always a safe answer, and is what null means here.
/// </summary>
[TestFixture]
public class DependencyWalkerTests
{
    [Test]
    public async Task AQueryReadsItsRoot()
    {
        var request = Capture()
            .Source<Order>("Order")
            .Select(_ => new {_.Region})
            .ToScryRequest();

        Assert.That(await Read(request), Is.EquivalentTo(Names<Order>()));
    }

    [Test]
    public async Task AReferenceNavigationReadsWhatItReaches()
    {
        var request = Capture()
            .Source<Employee>("Employee")
            .Select(_ => new {_.Name, Department = _.Department!.Name})
            .ToScryRequest();

        Assert.That(await Read(request), Is.EquivalentTo(Names<Employee, Department>()));
    }

    [Test]
    public async Task ANavigationInAPredicateCountsAsMuchAsOneInAProjection()
    {
        var request = Capture()
            .Source<Employee>("Employee")
            .Where(_ => _.Department!.Name == "Sales")
            .Select(_ => new {_.Name})
            .ToScryRequest();

        Assert.That(await Read(request), Is.EquivalentTo(Names<Employee, Department>()));
    }

    [Test]
    public async Task ACollectionNavigationReadsItsElements()
    {
        var request = Capture()
            .Source<Order>("Order")
            .Where(_ => _.Lines.Any())
            .Select(_ => new {_.Region})
            .ToScryRequest();

        Assert.That(await Read(request), Is.EquivalentTo(Names<Order, OrderLine>()));
    }

    // A derived type's rows are read through its root, and that is the name a write to either reports.
    [Test]
    public async Task ADerivedSourceReadsItsRoot()
    {
        var request = Capture()
            .Source<Vehicle>("Vehicle")
            .Select(_ => new {_.Name})
            .ToScryRequest();

        Assert.That(await Read(request), Is.EquivalentTo(Names<Asset>()));
    }

    // Whatever the terminal, the rows folded are the same rows.
    [Test]
    public Task ATerminalReadsWhatItsQueryDoes()
    {
        var orders = Capture()
            .Source<Order>("Order")
            .Where(_ => _.Lines.Any());

        return Assert.MultipleAsync(async () =>
        {
            Assert.That(await Read(orders.ToScryRequest(new CountOp())), Is.EquivalentTo(Names<Order, OrderLine>()));
            Assert.That(await Read(orders.ToScryRequest(new AnyOp(Predicate: null))), Is.EquivalentTo(Names<Order, OrderLine>()));
            Assert.That(
                await Read(orders.OrderBy(_ => _.Id).Select(_ => new {_.Id}).ToScryRequest(new FirstOp(OrDefault: true, Predicate: null))),
                Is.EquivalentTo(Names<Order, OrderLine>()));
            Assert.That(
                await Read(orders.OrderBy(_ => _.Id).Select(_ => new {_.Id}).ToScryRequest(new PageOp(Size: 1))),
                Is.EquivalentTo(Names<Order, OrderLine>()));
        });
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

        Assert.That(await Read(request, processor), Is.EquivalentTo(Names<Order, Department>()));
    }

    // Rows held in memory come from nowhere a write to the database could be seen to reach.
    [Test]
    public async Task APocoSourceCannotBeTold()
    {
        var request = Capture()
            .Source<Holiday>("Holiday")
            .Select(_ => new {_.Name})
            .ToScryRequest();

        Assert.That(await Read(request), Is.Null);
    }

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
