/// <summary>
/// The richer side pipelines: a join's inner side and a set operand may carry filters and an ordering
/// bounded by Skip/Take, not only a predicate. The wire carries them as <c>InnerOps</c> /
/// <c>OperandOps</c> under version 2, so a server predating the shape rejects the request whole
/// rather than reading the side partially — an ignored bound would answer with more rows than the
/// query asked for.
/// </summary>
[TestFixture]
public class SidePipelineTests
{
    // The inner side is the two highest-amount orders anywhere; joining on Region then pairs each
    // outer row only with inner rows that survived the bound.
    [Test]
    public async Task JoinsAgainstABoundedInnerSide()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Join(
                client.Source<Order>("Order").OrderByDescending(_ => _.Amount).Take(2),
                _ => _.Region,
                _ => _.Region,
                (outer, inner) => new {outer.Code, Matched = inner.Amount})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            // Top two by amount are 250 and 100, both North — so each North outer pairs with both,
            // and the South outer pairs with nothing.
            Assert.That(rows, Has.Count.EqualTo(4));
            Assert.That(rows.Where(_ => _.Code == "40").Select(_ => _.Matched).Order(), Is.EqualTo([100m, 250m]));
            Assert.That(rows.Where(_ => _.Code == "8").Select(_ => _.Matched).Order(), Is.EqualTo([100m, 250m]));
        });
    }

    [Test]
    public async Task AnInnerSideFiltersBeforeItBounds()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A-grades are 100 (North) and 75 (South); the bound keeps the dearest one.
        var rows = await client.Source<Order>("Order")
            .Join(
                client.Source<Order>("Order").Where(_ => _.Grade == 'A').OrderByDescending(_ => _.Amount).Take(1),
                _ => _.Region,
                _ => _.Region,
                (outer, inner) => new {outer.Code, Matched = inner.Amount})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(_ => _.Matched), Is.All.EqualTo(100m));
        });
    }

    // North rows, unioned with the single cheapest order anywhere.
    [Test]
    public async Task UnionsWithABoundedOperand()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "North")
            .Select(_ => new {_.Code, _.Amount})
            .Union(
                client.Source<Order>("Order")
                    .OrderBy(_ => _.Amount)
                    .Take(1)
                    .Select(_ => new {_.Code, _.Amount}))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Code).Order(), Is.EqualTo(["17", "40", "8"]));
    }

    // Skip slices from an ordered operand: the middle order by amount is the North "40", which Concat
    // then repeats rather than deduplicates.
    [Test]
    public async Task ConcatsASlicedOperand()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "North")
            .Select(_ => new {_.Code})
            .Concat(
                client.Source<Order>("Order")
                    .OrderBy(_ => _.Amount)
                    .Skip(1)
                    .Take(1)
                    .Select(_ => new {_.Code}))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Code).Order(), Is.EqualTo(["40", "40", "8"]));
    }

    // A request is stamped with the lowest version that carries it whole, so only the queries that
    // need the new shape are refused by a server predating it.
    [Test]
    public void StampsTheVersionThePipelineNeeds()
    {
        var plain = QueryRequest.Create("Order", [new CountOp()]);
        var richer = QueryRequest.Create(
            "Order",
            [
                new JoinOp(
                    "Order",
                    JoinKind.Inner,
                    new MemberNode(["Region"]),
                    new MemberNode(["Region"]),
                    null,
                    [new("Code", JoinSide.Outer, ["Code"])])
                {
                    InnerOps = [new OrderByOp(new MemberNode(["Amount"]), Descending: true), new TakeOp(1)]
                }
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(plain.Version, Is.EqualTo(1));
            Assert.That(richer.Version, Is.EqualTo(2));
        });
    }

    [Test]
    public void AnUnboundedOrderingIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Join(
                    client.Source<Order>("Order").OrderBy(_ => _.Amount),
                    _ => _.Region,
                    _ => _.Region,
                    (outer, inner) => new {outer.Code})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("bounded by Skip or Take"));
    }

    [Test]
    public void UnorderedPagingIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Join(
                    client.Source<Order>("Order").Take(1),
                    _ => _.Region,
                    _ => _.Region,
                    (outer, inner) => new {outer.Code})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("in that order"));
    }

    // The same grammar server-side, for a request that did not come through the translator.
    [Test]
    public void TheServerRefusesAnUnboundedOrdering()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new JoinOp(
                    "Order",
                    JoinKind.Inner,
                    new MemberNode(["Region"]),
                    new MemberNode(["Region"]),
                    null,
                    [new("Code", JoinSide.Outer, ["Code"])])
                {
                    InnerOps = [new OrderByOp(new MemberNode(["Amount"]), Descending: false)]
                }
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("bounded by Skip or Take"));
    }

    [Test]
    public void BothSpellingsOfTheInnerFilterAreRefused()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new JoinOp(
                    "Order",
                    JoinKind.Inner,
                    new MemberNode(["Region"]),
                    new MemberNode(["Region"]),
                    new BinaryNode(BinaryOp.Equal, new MemberNode(["Grade"]), new ConstNode("A", ClrTypeTag.String)),
                    [new("Code", JoinSide.Outer, ["Code"])])
                {
                    InnerOps = [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Grade"]), new ConstNode("A", ClrTypeTag.String)))]
                }
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("never both"));
    }

    [Test]
    public void AGroupByCannotCrossToTheSide()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new JoinOp(
                    "Order",
                    JoinKind.Inner,
                    new MemberNode(["Region"]),
                    new MemberNode(["Region"]),
                    null,
                    [new("Code", JoinSide.Outer, ["Code"])])
                {
                    InnerOps = [new GroupByOp([new MemberNode(["Region"])])]
                }
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("is not allowed on a join's inner side"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
