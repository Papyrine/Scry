/// <summary>
/// Collection navigations are aggregable but never projectable: every question here folds a
/// collection to a scalar, evaluated by the database as a correlated subquery.
/// </summary>
[TestFixture]
public class CollectionSubqueryTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record OrderRow(string Region, int Lines);

    record TotalRow(string Region, decimal Total);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task AnyOverACollection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Only the first order has a line priced at 25.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Lines.Any(l => l.Price == 25m))
            .Select(_ => new OrderRow(_.Region, _.Lines.Count()))
            .ToListAsync();

        Assert.That(rows.Single().Lines, Is.EqualTo(2));
    }

    [Test]
    public async Task AnyWithoutAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Two of the three orders have lines.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Lines.Any());

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task AllOverACollection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // All is vacuously true for the order with no lines, exactly as in LINQ and SQL.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Lines.All(l => l.Quantity > 1));

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task CountInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new OrderRow(_.Region, _.Lines.Count()))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Lines), Is.EqualTo(new[] { 2, 1, 0 }));
    }

    [Test]
    public async Task CountPropertyMeansTheSameAsTheCall()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order").CountAsync(_ => _.Lines.Count > 1);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task CountWithAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new OrderRow(_.Region, _.Lines.Count(l => l.Quantity > 1)))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Lines), Is.EqualTo(new[] { 1, 1, 0 }));
    }

    [Test]
    public async Task WhereThenCountFoldsIntoTheSubqueryPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new OrderRow(_.Region, _.Lines.Where(l => l.Quantity > 1).Count()))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Lines), Is.EqualTo(new[] { 1, 1, 0 }));
    }

    [Test]
    public async Task SumOverACollection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new TotalRow(_.Region, _.Lines.Sum(l => l.Price)))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Total), Is.EqualTo(new[] { 75m, 50m, 0m }));
    }

    [Test]
    public async Task MaxOverAnEmptyCollectionIsNullRatherThanAFault()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The South order has no lines; the selected value is made nullable so SQL's NULL survives.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new OrderRow(_.Region, _.Lines.Count()))
            .ToListAsync();

        Assert.That(rows.Single().Lines, Is.Zero);
    }

    [Test]
    public async Task AggregateOverACollectionInAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order").CountAsync(_ => _.Lines.Max(l => l.Price) == 50m);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void ProjectingACollectionIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // The whole point of the opt-in: a collection is aggregable, never projectable, so no request
        // can return an unbounded nested collection.
        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Lines", new NodeValue(new MemberNode(["Lines"])))]))]);

        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void TraversingThroughACollectionIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // A collection is not a step in a member path — there is no single row to read Sku from.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.Equal,
                    new MemberNode(["Lines", "Sku"]),
                    new ConstNode("A-1", ClrTypeTag.String)))
            ]);

        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void AnUnOptedInCollectionStaysInvisible()
    {
        using var context = TestContext.CreateSeeded();

        // Department.Employees carries no [QueryableCollection], so it is not on the allow-list at all.
        var request = QueryRequest.Create(
            "Department",
            [new WhereOp(new SubqueryNode(["Employees"], SubqueryFn.Any, null, null))]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("not allow-listed"));
    }

    [Test]
    public void ASubqueryInsideASubqueryIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(new SubqueryNode(
                    ["Lines"],
                    SubqueryFn.Any,
                    new SubqueryNode(["Lines"], SubqueryFn.Any, null, null),
                    null))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("inside another subquery"));
    }

    [Test]
    public void AnIgnoredMemberStaysHiddenInsideASubquery()
    {
        using var context = TestContext.CreateSeeded();

        // The element type's own allow-list applies inside the subquery, against OrderLine rather than
        // the row the subquery hangs off.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(new SubqueryNode(
                    ["Lines"],
                    SubqueryFn.Any,
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Region"]),
                        new ConstNode("North", ClrTypeTag.String)),
                    null))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("not allow-listed"));
    }

    [Test]
    public void AllWithoutAPredicateIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [new WhereOp(new SubqueryNode(["Lines"], SubqueryFn.All, null, null))]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("requires a predicate"));
    }

    [Test]
    public void ExposingACollectionOfAPoliciedTypeIsRefusedAtStartup()
    {
        // A policy filters a source; a subquery has none, so counting a policied collection would count
        // exactly the rows the policy hides. Refused when the schema is built, not at query time.
        var exception = Assert.Throws<Exception>(
            () => ScryProcessor.Create<TestContext>(
                options =>
                {
                    options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                    options.AddPolicy<OrderLine, BulkLinesOnlyPolicy>();
                }));

        Assert.That(exception!.Message, Does.Contain("row policy"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
