/// <summary>
/// A collection of values rather than of rows — an EF primitive collection, which the provider stores
/// as a JSON column. It aggregates like any other collection; the difference is that its elements have
/// no members, so a question about one reads the element itself.
/// </summary>
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

        await Assert.That(rows.Single().Region).IsEqualTo("North");
    }

    [Test]
    public async Task AnyWithAPredicateOverTheElement()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order").CountAsync(_ => _.Tags.Any(tag => tag == "export"));

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task AFunctionOverTheElement()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The element is a value, so the string functions apply to it directly.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Tags.Any(tag => tag.StartsWith("ex")));

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task AllOverACollectionOfValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // True for the second order and vacuously true for the third, whose collection is empty.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Tags.All(tag => tag != "urgent"));

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountOfACollectionOfValues()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new TagRow(_.Region, _.Tags.Count))
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Tags)).IsEquivalentTo([2, 1, 0], CollectionOrdering.Matching);
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

        await Assert.That(rows.Select(_ => _.Total)).IsEquivalentTo([8, 8, 0], CollectionOrdering.Matching);

        // Max over the empty collection is null rather than a fault, as it is over a collection of rows.
        await Assert.That(rows.Select(_ => _.Best)).IsEquivalentTo(new int?[] {5, 8, null}, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnAggregateOverValuesInAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order").CountAsync(_ => _.Scores.Sum() > 7);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task AnEnumElementRidesTheWireAsItsName()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Priority is reachable only through this collection, so this covers both halves: the enum is
        // re-emitted to clients at all, and its value name resolves back to the enum server-side.
        var count = await client.Source<Order>("Order").CountAsync(_ => _.Priorities.Contains(Priority.Low));

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task AnUnOptedInCollectionOfValuesStaysInvisible()
    {
        using var context = TestContext.CreateSeeded();

        // Order.Notes carries no [QueryableCollection]. Being a collection of values changes nothing:
        // default-deny applies to the member.
        var request = QueryRequest.Create(
            "Order",
            [new WhereOp(new SubqueryNode(["Notes"], SubqueryFn.Any))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("not allow-listed");
    }

    [Test]
    public async Task ReadingAMemberOfAValueElementIsRejected()
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
                        new ConstNode("6", ClrTypeTag.Int32))))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("has no members");
    }

    [Test]
    public async Task ReadingAnElementOutsideASubqueryIsRejected()
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("subquery over a collection of values");
    }

    [Test]
    public async Task ReadingAnElementInsideACollectionOfRowsIsRejected()
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
                        new ConstNode("A-1", ClrTypeTag.String))))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("subquery over a collection of values");
    }

    [Test]
    public async Task FlatteningACollectionOfValuesIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // The rows a flatten would produce are bare values, and every operator after it names members
        // of the row it reads.
        var request = QueryRequest.Create("Order", [new SelectManyOp(["Tags"])]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("cannot be flattened");
    }

    [Test]
    public void ProjectingACollectionOfValuesIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Aggregable, never projectable — the same bound on the response shape as any other collection.
        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Tags", new NodeValue(new MemberNode(["Tags"])))]))]);

        Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public async Task CorrelatingAContainsWithTheRowIsRefusedByTheClient()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The test reads the collection's elements, where the owning row is not in scope. Refused where
        // it is written rather than sent as a request the server would reject.
        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Where(_ => _.Tags.Contains(_.Region))
                .Select(_ => new {_.Region})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("takes a constant");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
