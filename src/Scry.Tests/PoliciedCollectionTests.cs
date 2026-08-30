/// <summary>
/// A <c>[QueryableCollection]</c> of a policied type. A subquery has no source of its own, so counting
/// the collection off the owner would count exactly the rows the policy hides — which is why exposing
/// one is refused until the policy says how it wants to be read through.
/// </summary>
[TestFixture]
public class PoliciedCollectionTests
{
    [Test]
    public void ExposingOneIsRefusedUntilThePolicySaysHowToReadIt()
    {
        // The default, and what the server did before there was anything else to say: a policy that has
        // not been asked the question does not get guessed at.
        var exception = Assert.Throws<Exception>(
            () => Build(_ => _.AddPolicy<OrderLine, BulkLinesOnlyPolicy>()))!;

        Assert.That(exception.Message, Does.Contain("Order.Lines"));
        Assert.That(exception.Message, Does.Contain("CollectionNavigation"));
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

        Assert.That(rows.Select(_ => _.Lines), Is.EqualTo([0, 1, 1]));
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

        Assert.That(rows.Select(_ => _.Total), Is.EqualTo([25m, 50m]));
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

        Assert.That(rows.Select(_ => _.Sku), Is.EqualTo(["A-1", "B-1"]));
    }

    [Test]
    public void ErroringFailsTheRequestWhereAnElementWasDenied()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Erroring());

        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Order>("Order")
                .Select(_ => new
                {
                    Lines = _.Lines.Count
                })
                .ToListAsync());
    }

    [Test]
    public void ACollectionNobodyReadsDeniesNothing()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Erroring());

        // The denial is reported for reading through the collection, not for the policy existing. A
        // query that never names the member is unaffected by how it would have answered.
        Assert.DoesNotThrowAsync(
            () => client.Source<Order>("Order")
                .Select(_ => new {_.Region})
                .ToListAsync());
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
