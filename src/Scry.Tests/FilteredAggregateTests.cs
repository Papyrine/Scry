/// <summary>
/// Composed aggregates over a group: a <c>Where</c> before the fold filters the rows —
/// <c>Count(predicate)</c> abbreviates it — and <c>Select</c> + <c>Distinct</c> folds only the
/// distinct selected values. EF folds the filter into the aggregate itself (<c>SUM(CASE WHEN … END)</c>,
/// <c>COUNT(DISTINCT …)</c>), and the composed fields travel under wire version 2, so a server
/// predating them rejects the request rather than folding unfiltered.
/// </summary>
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
                AGraded = _
                    .Where(_ => _.Grade == 'A')
                    .Sum(_ => _.Amount)
            })
            .ToListAsync();

        using (Assert.Multiple())
        {
            var north = regions.Single(_ => _.Key == "North");
            await Assert.That(north.Big).IsEqualTo(2);
            await Assert.That(north.AGraded).IsEqualTo(100m);

            var south = regions.Single(_ => _.Key == "South");
            await Assert.That(south.Big).IsZero();
            await Assert.That(south.AGraded).IsEqualTo(75m);
        }
    }

    [Test]
    public async Task CountWithAPredicateAbbreviatesTheWhere()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new {_.Key, Big = _.Count(_ => _.Amount > 90)})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(regions.Single(_ => _.Key == "North").Big).IsEqualTo(2);
            await Assert.That(regions.Single(_ => _.Key == "South").Big).IsZero();
        }
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
                Grades = _.Select(_ => _.Grade).Distinct().Count()
            })
            .ToListAsync();

        var group = groups.Single();
        using (Assert.Multiple())
        {
            await Assert.That(group.Rows).IsEqualTo(3);
            await Assert.That(group.Grades).IsEqualTo(2);
        }
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

        await Assert.That(regions.Single().Key).IsEqualTo("North");
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
            .Select(_ => new {_.Key, Discounts = _.Select(_ => _.Discount).Distinct().Count()})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(regions.Single(_ => _.Key == "North").Discounts).IsEqualTo(1);
            await Assert.That(regions.Single(_ => _.Key == "South").Discounts).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheComposedFieldsTravelUnderVersion2()
    {
        var plain = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Rows", new NodeValue(new AggregateNode(AggregateFn.Count)))]))
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
                                new AggregateNode(AggregateFn.Count)
                                {
                                    Predicate = new BinaryNode(BinaryOp.GreaterThan, new MemberNode(["Amount"]), new ConstNode("90", ClrTypeTag.Decimal))
                                }))
                    ]))
            ]);

        using (Assert.Multiple())
        {
            await Assert.That(plain.Version).IsEqualTo(1);
            await Assert.That(filtered.Version).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ADistinctFoldWithoutASelectorIsRejected()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Rows",
                            new NodeValue(
                                new AggregateNode(AggregateFn.Count)
                                {
                                    Distinct = true
                                }))
                    ]))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("requires a selector");
    }

    [Test]
    public async Task TheTextAggregateStaysWhole()
    {
        await using var context = TestContext.CreateSeeded();

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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("folds the whole group");
    }

    [Test]
    public async Task AFoldOverSelectedValuesRefusesAFilterWrittenAfterTheSelect()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new {Total = _.Select(_ => _.Amount).Where(_ => _ > 90).Sum()})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("filter the rows, then select the values");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
