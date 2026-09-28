/// <summary>
/// Flattening a [QueryableCollection] into its elements. Unlike an aggregate over a collection, which
/// folds it to a scalar, this replaces the row being queried — so the element type has to stand on
/// its own allow-list, which it already does.
/// </summary>
public class SelectManyTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record Line(string Sku, int Quantity);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task FlattensACollection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientSelectMany
        var lines = await client.Source<Order>("Order")
            .SelectMany(_ => _.Lines)
            .Select(_ => new Line(_.Sku, _.Quantity))
            .ToListAsync();
        // end-snippet

        await Assert.That(lines.Select(_ => _.Sku).Order()).IsEquivalentTo(["A-1", "A-2", "B-1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FiltersTheRowsBeforeAndTheElementsAfter()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The first Where reads the order, the second reads the line — the flatten is what changes
        // which row the rest of the pipeline is written against.
        var lines = await client.Source<Order>("Order")
            .Where(_ => _.Region == "North")
            .SelectMany(_ => _.Lines)
            .Where(_ => _.Quantity > 1)
            .Select(_ => new Line(_.Sku, _.Quantity))
            .ToListAsync();

        await Assert.That(lines.Select(_ => _.Sku).Order()).IsEquivalentTo(["A-1", "B-1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OrdersAndPagesTheFlattenedElements()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var lines = await client.Source<Order>("Order")
            .SelectMany(_ => _.Lines)
            .OrderBy(_ => _.Sku)
            .Skip(1)
            .Take(1)
            .Select(_ => new Line(_.Sku, _.Quantity))
            .ToListAsync();

        await Assert.That(lines.Single().Sku).IsEqualTo("A-2");
    }

    [Test]
    public async Task CountsTheFlattenedElements()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order")
            .SelectMany(_ => _.Lines)
            .CountAsync();

        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task AggregatesOverTheFlattenedElements()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var total = await client.Source<Order>("Order")
            .SelectMany(_ => _.Lines)
            .SumAsync(_ => _.Price);

        await Assert.That(total).IsEqualTo(125m);
    }

    [Test]
    public async Task GroupsTheFlattenedElements()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .SelectMany(_ => _.Lines)
            .GroupBy(_ => _.Quantity)
            .Select(_ => new {Quantity = _.Key, Count = _.Count()})
            .ToListAsync();

        await Assert.That(rows.Sum(_ => _.Count)).IsEqualTo(3);
    }

    [Test]
    public async Task RejectsFlatteningAMemberThatIsNotACollection()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .SelectMany(_ => _.Region)
                .Select(_ => new {Value = _})
                .ToListAsync());

        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task RejectsASecondFlatten()
    {
        using var context = TestContext.CreateSeeded();

        // The model has no collection of collections, so a second flatten is not something a
        // generated client can write — which is exactly why the guard is tested on the wire instead.
        var request = QueryRequest.Create(
            "Order",
            [
                new SelectManyOp(["Lines"]),
                new SelectManyOp(["Lines"])
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Only one SelectMany is allowed.");
    }

    [Test]
    public async Task RejectsFlatteningAMemberThatIsNotAQueryableCollection()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create("Order", [new SelectManyOp(["Region"])]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("is not a queryable collection");
    }

    [Test]
    public async Task RejectsFlatteningAfterASelect()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => client.Source<Order>("Order")
                .Select(_ => new {_.Lines})
                .SelectMany(_ => _.Lines)
                .Select(_ => new Line(_.Sku, _.Quantity))
                .ToListAsync());

        await Assert.That(exception).IsNotNull();
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
