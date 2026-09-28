/// <summary>
/// Grouping by a key the query computes rather than a member it reads. A member key names itself on
/// the wire by its path, which is what the server matches it back by; a computed key has no path, so
/// it is named by its position among the query's keys instead.
/// </summary>
public class ComputedGroupKeyTests
{
    [Test]
    public async Task GroupsByAFunctionOfAMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientComputedGroupKey
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Placed.DayOfWeek)
            .Select(_ => new {Day = _.Key, Count = _.Count()})
            .ToListAsync();
        // end-snippet

        var expected = context.Orders
            .ToList()
            .GroupBy(_ => _.Placed.DayOfWeek)
            .ToDictionary(_ => _.Key, _ => _.Count());

        await Assert.That(rows).Count().IsEqualTo(expected.Count);
        using (Assert.Multiple())
        {
            foreach (var row in rows)
            {
                await Assert.That(row.Count).IsEqualTo(expected[row.Day]).Because($"{row.Day}");
            }
        }
    }

    [Test]
    public async Task GroupsByAStringFunction()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region.ToUpper())
            .Select(_ => new {Region = _.Key, Total = _.Sum(_ => _.Amount)})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Region).Order()).IsEquivalentTo(["NORTH", "SOUTH"], CollectionOrdering.Matching);
        await Assert.That(rows.Single(_ => _.Region == "NORTH").Total).IsEqualTo(350m);
    }

    [Test]
    public async Task GroupsByAnArithmeticExpression()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Amount * 2)
            .Select(_ => new {Doubled = _.Key, Count = _.Count()})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Doubled).Order()).IsEquivalentTo([150m, 200m, 500m], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FiltersGroupsByAComputedKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // HAVING reads the computed key the same way the projection does.
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region.ToUpper())
            .Where(_ => _.Key == "NORTH")
            .Select(_ => new {Region = _.Key, Count = _.Count()})
            .ToListAsync();

        await Assert.That(rows.Single().Count).IsEqualTo(2);
    }

    [Test]
    public async Task ComposesAComputedKeyWithAnAggregate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region.ToUpper())
            .Select(_ => new {Label = _.Key + "!", Average = _.Sum(_ => _.Amount) / _.Count()})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Label).Order()).IsEquivalentTo(["NORTH!", "SOUTH!"], CollectionOrdering.Matching);
        await Assert.That(rows.Single(_ => _.Label == "NORTH!").Average).IsEqualTo(175m);
    }

    [Test]
    public async Task MixesAComputedPartIntoACompositeKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // One part is a plain member and names itself by path; the other is computed and names itself
        // by position. Both resolve back to the slot they were grouped at.
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, Doubled = _.Amount * 2})
            .Select(_ => new {_.Key.Region, _.Key.Doubled, Count = _.Count()})
            .ToListAsync();

        await Assert.That(rows).Count().IsEqualTo(3);
        await Assert.That(rows.Single(_ => _.Doubled == 500m).Region).IsEqualTo("North");
    }

    [Test]
    public async Task RejectsAGroupKeyOutsideAGroupedQuery()
    {
        using var context = TestContext.CreateSeeded();

        // No generated client can write this — the node only exists inside a grouped projection — so
        // the guard is tested on the wire.
        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Key", new NodeValue(new GroupKeyNode(0)))]))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("only be read in the Select or Where that follows a GroupBy");
    }

    [Test]
    public async Task RejectsAGroupKeyBeyondTheKeysTheQueryHas()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Key", new NodeValue(new GroupKeyNode(3)))]))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Group key 3 is out of range");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
