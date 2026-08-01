/// <summary>
/// A collection of values rather than of rows — an EF primitive collection, which the provider stores
/// as a JSON column. It aggregates like any other collection; the difference is that its elements have
/// no members, so a question about one reads the element itself.
/// </summary>
[TestFixture]
public class PrimitiveCollectionTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record TagRow(string Region, int Tags);

    record ScoreRow(string Region, int Total, int? Best);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ContainsOverACollectionOfValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientPrimitiveCollectionContains
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Tags.Contains("urgent"))
            .Select(_ => new {_.Region})
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Single().Region, Is.EqualTo("North"));
    }

    [Test]
    public async Task AnyWithAPredicateOverTheElement()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order").CountAsync(_ => _.Tags.Any(tag => tag == "export"));

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task AFunctionOverTheElement()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The element is a value, so the string functions apply to it directly.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Tags.Any(tag => tag.StartsWith("ex")));

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task AllOverACollectionOfValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // True for the second order and vacuously true for the third, whose collection is empty.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Tags.All(tag => tag != "urgent"));

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task CountOfACollectionOfValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new TagRow(_.Region, _.Tags.Count()))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Tags), Is.EqualTo([2, 1, 0]));
    }

    [Test]
    public async Task AggregatesFoldTheElementsThemselves()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientPrimitiveCollectionAggregate
        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new ScoreRow(_.Region, _.Scores.Sum(), _.Scores.Max()))
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Select(_ => _.Total), Is.EqualTo([8, 8, 0]));

        // Max over the empty collection is null rather than a fault, as it is over a collection of rows.
        Assert.That(rows.Select(_ => _.Best), Is.EqualTo(new int?[] {5, 8, null}));
    }

    [Test]
    public async Task AnAggregateOverValuesInAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order").CountAsync(_ => _.Scores.Sum() > 7);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task AnEnumElementRidesTheWireAsItsName()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Priority is reachable only through this collection, so this covers both halves: the enum is
        // re-emitted to clients at all, and its value name resolves back to the enum server-side.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Priorities.Contains(Priority.Low));

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void AnUnOptedInCollectionOfValuesStaysInvisible()
    {
        using var context = TestContext.CreateSeeded();

        // Order.Notes carries no [QueryableCollection]. Being a collection of values changes nothing:
        // default-deny applies to the member.
        var request = QueryRequest.Create(
            "Order",
            [new WhereOp(new SubqueryNode(["Notes"], SubqueryFn.Any, null, null))]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("not allow-listed"));
    }

    [Test]
    public void ReadingAMemberOfAValueElementIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // A string element has no allow-listed members — not even the ones the CLR type has.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(new SubqueryNode(
                    ["Tags"],
                    SubqueryFn.Any,
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Length"]),
                        new ConstNode("6", ClrTypeTag.Int32)),
                    null))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("has no members"));
    }

    [Test]
    public void ReadingAnElementOutsideASubqueryIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // An element node names the row it is read against. Outside a subquery over values that row is
        // an entity, so allowing it would let a query compare a whole row to a constant.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.Equal,
                    new ElementNode(),
                    new ConstNode("urgent", ClrTypeTag.String)))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("subquery over a collection of values"));
    }

    [Test]
    public void ReadingAnElementInsideACollectionOfRowsIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Order.Lines holds rows, so its element is an OrderLine — a whole row, which is not a value a
        // query may compare. Its members are what a predicate reads.
        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(new SubqueryNode(
                    ["Lines"],
                    SubqueryFn.Any,
                    new BinaryNode(
                        BinaryOp.Equal,
                        new ElementNode(),
                        new ConstNode("A-1", ClrTypeTag.String)),
                    null))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("subquery over a collection of values"));
    }

    [Test]
    public void FlatteningACollectionOfValuesIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // The rows a flatten would produce are bare values, and every operator after it names members
        // of the row it reads.
        var request = QueryRequest.Create("Order", [new SelectManyOp(["Tags"])]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("cannot be flattened"));
    }

    [Test]
    public void ProjectingACollectionOfValuesIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Aggregable, never projectable — the same bound on the response shape as any other collection.
        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Tags", new NodeValue(new MemberNode(["Tags"])))]))]);

        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void CorrelatingAContainsWithTheRowIsRefusedByTheClient()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The test reads the collection's elements, where the owning row is not in scope. Refused where
        // it is written rather than sent as a request the server would reject.
        var exception = Assert.ThrowsAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Where(_ => _.Tags.Contains(_.Region))
                .Select(_ => new {_.Region})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("takes a constant"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
