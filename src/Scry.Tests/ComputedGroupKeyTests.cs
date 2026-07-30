/// <summary>
/// Grouping by a key the query computes rather than a member it reads. A member key names itself on
/// the wire by its path, which is what the server matches it back by; a computed key has no path, so
/// it is named by its position among the query's keys instead.
/// </summary>
[TestFixture]
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

        Assert.That(rows, Has.Count.EqualTo(expected.Count));
        Assert.Multiple(() =>
        {
            foreach (var row in rows)
            {
                Assert.That(row.Count, Is.EqualTo(expected[row.Day]), $"{row.Day}");
            }
        });
    }

    [Test]
    public async Task GroupsByAStringFunction()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region.ToUpper())
            .Select(_ => new {Region = _.Key, Total = _.Sum(o => o.Amount)})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Region).Order(), Is.EqualTo(["NORTH", "SOUTH"]));
        Assert.That(rows.Single(_ => _.Region == "NORTH").Total, Is.EqualTo(350m));
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

        Assert.That(rows.Select(_ => _.Doubled).Order(), Is.EqualTo([150m, 200m, 500m]));
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

        Assert.That(rows.Single().Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ComposesAComputedKeyWithAnAggregate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region.ToUpper())
            .Select(_ => new {Label = _.Key + "!", Average = _.Sum(o => o.Amount) / _.Count()})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Label).Order(), Is.EqualTo(["NORTH!", "SOUTH!"]));
        Assert.That(rows.Single(_ => _.Label == "NORTH!").Average, Is.EqualTo(175m));
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

        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows.Single(_ => _.Doubled == 500m).Region, Is.EqualTo("North"));
    }

    [Test]
    public void RejectsAGroupKeyOutsideAGroupedQuery()
    {
        using var context = TestContext.CreateSeeded();

        // No generated client can write this — the node only exists inside a grouped projection — so
        // the guard is tested on the wire.
        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Key", new NodeValue(new GroupKeyNode(0)))]))]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("only be read in the Select or Where that follows a GroupBy"));
    }

    [Test]
    public void RejectsAGroupKeyBeyondTheKeysTheQueryHas()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Key", new NodeValue(new GroupKeyNode(3)))]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Group key 3 is out of range"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
