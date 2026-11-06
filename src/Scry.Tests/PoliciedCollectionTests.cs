/// <summary>
/// A <c>[QueryableCollection]</c> of a policied type. A subquery has no source of its own, so counting
/// the collection off the owner would count exactly the rows the policy hides — which is why the
/// collection is read through the policy, unless the policy says it would rather be refused.
/// </summary>
public class PoliciedCollectionTests
{
    [Test]
    public async Task ReadingThroughThePolicyIsTheDefault()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Build(_ => _.AddPolicy<OrderLine, BulkLinesOnlyPolicy>()));

        // Registered with nothing said about collections, the policy hides: the same answer Hiding()
        // gives below, and the one a navigation into the type already gave.
        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Amount)
            .Select(_ => new {_.Region, Lines = _.Lines.Count})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Lines)).IsEquivalentTo([0, 1, 1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task APolicySetToRefuseIsAStartupFailureNamingTheFix()
    {
        var exception = Assert.ThrowsExactly<Exception>(
            () => Build(_ => _.AddPolicy<OrderLine, BulkLinesOnlyPolicy>(new()
            {
                CollectionNavigation = DeniedCollectionMode.Refuse
            })));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("'Order.Lines'");
            await Assert.That(exception.Message).Contains("'OrderLine'");
            await Assert.That(exception.Message).Contains("'BulkLinesOnlyPolicy'");
            await Assert.That(exception.Message).Contains("CollectionNavigation = DeniedCollectionMode.Hide");
        }
    }

    [Test]
    public async Task AProjectedCollectionReturnsOnlyTheAllowedElements()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Build(_ => _.AddPolicy<OrderLine, BulkLinesOnlyPolicy>()));

        // The policy denies the first order's single-quantity line, so returning the lines returns
        // exactly what a direct query of OrderLine would have.
        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Amount)
            .Select(_ => new {_.Region, Skus = _.Lines.Select(l => l.Sku).ToList()})
            .ToListAsync();

        await Assert.That(rows.SelectMany(_ => _.Skus)).IsEquivalentTo(["A-1", "B-1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task HidingReadsTheCollectionThroughThePolicy()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Hiding());

        // The first order has two lines and the policy denies one of them, so the count it answers with
        // is the count a direct query of OrderLine would have reached — not what the owner holds.
        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Amount)
            .Select(_ => new
            {
                _.Region,
                Lines = _.Lines.Count
            })
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Lines)).IsEquivalentTo([0, 1, 1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnAggregateOverTheCollectionIsFilteredToo()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Hiding());

        // Summing is the same question as counting: the denied line's price must not be in the total.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "North")
            .OrderBy(_ => _.Amount)
            .Select(_ => new {Total = _.Lines.Sum(line => line.Price)})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Total)).IsEquivalentTo([25m, 50m], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FlatteningTheCollectionReachesOnlyTheAllowedElements()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Hiding());

        // The other route to a collection's elements. Flattening reads them as rows rather than folding
        // them into a number, so a denied element left in would be handed over outright.
        var rows = await client.Source<Order>("Order")
            .SelectMany(_ => _.Lines)
            .OrderBy(_ => _.Sku)
            .Select(_ => new {_.Sku})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Sku)).IsEquivalentTo(["A-1", "B-1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ErroringFailsTheRequestWhereAnElementWasDenied()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Erroring());

        await Assert.ThrowsExactlyAsync<ScryPermissionException>(
            () => client.Source<Order>("Order")
                .Select(_ => new
                {
                    Lines = _.Lines.Count
                })
                .ToListAsync());
    }

    [Test]
    public async Task ACollectionNobodyReadsDeniesNothing()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Erroring());

        // The denial is reported for reading through the collection, not for the policy existing. A
        // query that never names the member is unaffected by how it would have answered.
        await Assert.That(async () => await client.Source<Order>("Order")
                .Select(_ => new {_.Region})
                .ToListAsync()).ThrowsNothing();
    }

    static ScryProcessor Hiding() =>
        Build(_ => _.AddPolicy<OrderLine, BulkLinesOnlyPolicy>(new()
        {
            CollectionNavigation = DeniedCollectionMode.Hide
        }));

    static ScryProcessor Erroring() =>
        Build(_ => _.AddPolicy<OrderLine, BulkLinesOnlyPolicy>(new()
        {
            CollectionNavigation = DeniedCollectionMode.Error
        }));

    static ScryProcessor Build(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });

    static ScryClient ClientFor(TestContext context, ScryProcessor processor) =>
        new((request, _) => Task.FromResult(processor.Execute(request, context)));
}
