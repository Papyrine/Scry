/// <summary>
/// Composed aggregates over a group: a <c>Where</c> before the fold filters the rows —
/// <c>Count(predicate)</c> abbreviates it — and <c>Select</c> + <c>Distinct</c> folds only the
/// distinct selected values. EF folds the filter into the aggregate itself (<c>SUM(CASE WHEN … END)</c>,
/// <c>COUNT(DISTINCT …)</c>), and the composed fields travel under wire version 2, so a server
/// predating them rejects the request rather than folding unfiltered.
/// </summary>
[TestFixture]
public class FilteredAggregateTests
{
    [Test]
    public async Task FiltersTheRowsAFoldReads()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new
            {
                _.Key,
                Big = _.Count(_ => _.Amount > 90),
                AGraded = _.Where(x => x.Grade == 'A').Sum(x => x.Amount)
            })
            .ToListAsync();

        Assert.Multiple(() =>
        {
            var north = regions.Single(_ => _.Key == "North");
            Assert.That(north.Big, Is.EqualTo(2));
            Assert.That(north.AGraded, Is.EqualTo(100m));

            var south = regions.Single(_ => _.Key == "South");
            Assert.That(south.Big, Is.Zero);
            Assert.That(south.AGraded, Is.EqualTo(75m));
        });
    }

    [Test]
    public async Task CountWithAPredicateAbbreviatesTheWhere()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new {_.Key, Big = _.Count(x => x.Amount > 90)})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(regions.Single(_ => _.Key == "North").Big, Is.EqualTo(2));
            Assert.That(regions.Single(_ => _.Key == "South").Big, Is.Zero);
        });
    }

    // Region names are all five letters, so the computed key folds every order into one group — whose
    // three rows carry only two distinct grades, which is what tells COUNT(DISTINCT) from COUNT.
    [Test]
    public async Task DistinctFoldsTheDistinctValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var groups = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region.Length)
            .Select(_ => new
            {
                Rows = _.Count(),
                Grades = _.Select(x => x.Grade).Distinct().Count()
            })
            .ToListAsync();

        var group = groups.Single();
        Assert.Multiple(() =>
        {
            Assert.That(group.Rows, Is.EqualTo(3));
            Assert.That(group.Grades, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task HavingReadsAFilteredCount()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Where(_ => _.Count(_ => _.Amount > 90) == 2)
            .Select(_ => new {_.Key})
            .ToListAsync();

        Assert.That(regions.Single().Key, Is.EqualTo("North"));
    }

    // A distinct fold over an optional member: SQL's distinct aggregates skip nulls, and the server
    // filters them in memory too, so the two North discounts — 10 and an absent one — count as one.
    [Test]
    public async Task ADistinctFoldSkipsAbsentValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new {_.Key, Discounts = _.Select(x => x.Discount).Distinct().Count()})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(regions.Single(_ => _.Key == "North").Discounts, Is.EqualTo(1));
            Assert.That(regions.Single(_ => _.Key == "South").Discounts, Is.EqualTo(1));
        });
    }

    [Test]
    public void TheComposedFieldsTravelUnderVersion2()
    {
        var plain = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Rows", new NodeValue(new AggregateNode(AggregateFn.Count, null)))]))
            ]);

        var filtered = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new(
                            "Big",
                            new NodeValue(
                                new AggregateNode(AggregateFn.Count, null)
                                {
                                    Predicate = new BinaryNode(BinaryOp.GreaterThan, new MemberNode(["Amount"]), new ConstNode("90", ClrTypeTag.Decimal))
                                }))
                    ]))
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(plain.Version, Is.EqualTo(1));
            Assert.That(filtered.Version, Is.EqualTo(2));
        });
    }

    [Test]
    public void ADistinctFoldWithoutASelectorIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Rows",
                            new NodeValue(
                                new AggregateNode(AggregateFn.Count, null)
                                {
                                    Distinct = true
                                }))
                    ]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("requires a selector"));
    }

    [Test]
    public void TheTextAggregateStaysWhole()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new(
                            "Codes",
                            new NodeValue(
                                new AggregateNode(AggregateFn.Join, new MemberNode(["Code"]), ", ")
                                {
                                    Predicate = new BinaryNode(BinaryOp.GreaterThan, new MemberNode(["Amount"]), new ConstNode("90", ClrTypeTag.Decimal))
                                }))
                    ]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("folds the whole group"));
    }

    [Test]
    public void AFoldOverSelectedValuesRefusesAFilterWrittenAfterTheSelect()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new {Total = _.Select(x => x.Amount).Where(v => v > 90).Sum()})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("filter the rows, then select the values"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
