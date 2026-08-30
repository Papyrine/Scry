/// <summary>
/// <c>string.Join</c> over a group — the text aggregate, SQL's <c>STRING_AGG</c>. The joined values
/// are ordered by themselves: SQL leaves the concatenation order unspecified, so the server imposes
/// one — <c>WITHIN GROUP</c> on SQL Server, the same <c>OrderBy</c> in memory — and the answer reads
/// identically from either source.
/// </summary>
[TestFixture]
public class StringJoinAggregateTests
{
    [Test]
    public async Task JoinsTheGroupsValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // North holds codes "40" and "8"; ordered by themselves as text, "40" sorts first.
        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new {Region = _.Key, Codes = string.Join(",", _.Select(x => x.Code))})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(regions.Single(_ => _.Region == "North").Codes, Is.EqualTo("40,8"));
            Assert.That(regions.Single(_ => _.Region == "South").Codes, Is.EqualTo("17"));
        });
    }

    // string.Concat is string.Join's empty-separator spelling, and reaches the wire as exactly that.
    [Test]
    public async Task ConcatJoinsWithNothingBetween()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new {Region = _.Key, Codes = string.Concat(_.Select(x => x.Code))})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(regions.Single(_ => _.Region == "North").Codes, Is.EqualTo("408"));
            Assert.That(regions.Single(_ => _.Region == "South").Codes, Is.EqualTo("17"));
        });
    }

    // Like Join, Concat folds the whole group: the composed forms stay off the text aggregate.
    [Test]
    public void AFilteredConcatIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new {Codes = string.Concat(_.Where(x => x.Amount > 90).Select(x => x.Code))})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("folds the whole group"));
    }

    [Test]
    public void AConcatOverSomethingNotTextIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new {Codes = string.Concat(_.Select(x => x.Amount))})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("select a string member"));
    }

    // The result-selector spelling unfolds into the same GroupBy + Select, so the aggregate reads
    // identically through it.
    [Test]
    public async Task JoinsThroughAResultSelector()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region, (region, orders) => new {Region = region, Codes = string.Join("|", orders.Select(x => x.Code))})
            .ToListAsync();

        Assert.That(regions.Single(_ => _.Region == "North").Codes, Is.EqualTo("40|8"));
    }

    [Test]
    public async Task ComposesWithTheOtherAggregates()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new {Region = _.Key, Codes = string.Join(", ", _.Select(x => x.Code)), Total = _.Sum(x => x.Amount)})
            .ToListAsync();

        var north = regions.Single(_ => _.Region == "North");
        Assert.Multiple(() =>
        {
            Assert.That(north.Codes, Is.EqualTo("40, 8"));
            Assert.That(north.Total, Is.EqualTo(350m));
        });
    }

    // A non-string selector binds the generic string.Join overload, which is the refusal's cue.
    [Test]
    public void ANonTextSelectorIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new {Amounts = string.Join(',', _.Select(x => x.Amount))})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("joins text"));
    }

    // The separator travels only on Join: any other aggregate carrying one is a malformed request.
    [Test]
    public void ASeparatorOnAnotherAggregateIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Region", new NodeValue(new MemberNode(["Region"]))),
                        new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"]), ",")))
                    ]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("does not take a separator"));
    }

    [Test]
    public void AJoinWithoutASeparatorIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Codes", new NodeValue(new AggregateNode(AggregateFn.Join, new MemberNode(["Code"]))))]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Join requires a separator"));
    }

    [Test]
    public void AJoinOverSomethingNotTextIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Amounts", new NodeValue(new AggregateNode(AggregateFn.Join, new MemberNode(["Amount"]), ",")))]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Join aggregates text"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
