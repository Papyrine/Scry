/// <summary>
/// A collection navigation may be returned as a list inside a row — one value or one flat object of
/// values per element — where every other question about it folds it to a scalar. One level only:
/// nothing inside the list may be an object, a subquery or another collection.
/// </summary>
public class CollectionProjectionTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record SkuRow(string Region, List<string> Skus);

    record LineRow(string Sku, decimal Price);

    record LinesRow(string Region, List<LineRow> Lines);

    record LineCountRow(string Sku, int Siblings);

    record NestedLineRow(string Sku, LineRow Again);

    record OrderCard(string Region, OrderLines Lines);

    record OrderLines(List<string> Skus);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ValuesSelectedFromRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new SkuRow(_.Region, _.Lines.Select(l => l.Sku).ToList()))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task ACollectionOfValuesAsItself()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Region, Tags = _.Tags.ToList()})
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task ACollectionOfValuesFiltered()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Region, Tags = _.Tags.Where(t => t != "export").ToList()})
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task ACollectionOfValuesComputed()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Region, Doubled = _.Scores.Select(s => s * 2).ToArray()})
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task FlatObjectsSelectedFromFilteredRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new LinesRow(
                _.Region,
                _.Lines.Where(l => l.Quantity > 1).Select(l => new LineRow(l.Sku, l.Price)).ToList()))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task AProjectedCollectionIsAPagesRowToo()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var page = await client.Source<Order>("Order")
            .OrderBy(_ => _.Id)
            .Select(_ => new SkuRow(_.Region, _.Lines.Select(l => l.Sku).ToList()))
            .ToPageAsync(pageSize: 2);

        await Verify(page.Items);
    }

    [Test]
    public Task TheWireCarriesTheCollectionAsAProjectionMember()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var request = client.Source<Order>("Order")
            .Select(_ => new LinesRow(
                _.Region,
                _.Lines.Where(l => l.Quantity > 1).Select(l => new LineRow(l.Sku, l.Price)).ToList()))
            .ToScryRequest();

        return Verify(request);
    }

    [Test]
    public async Task AnObjectInsideAnElementIsRefusedByTheClient()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.Throws<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new
                {
                    _.Region,
                    Lines = _.Lines.Select(l => new NestedLineRow(l.Sku, new(l.Sku, l.Price))).ToList()
                })
                .ToScryRequest());

        await Assert.That(exception.Message).Contains("nests an object inside a projected collection");
    }

    [Test]
    public async Task ASubqueryInsideAnElementIsRefusedByTheClient()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.Throws<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new
                {
                    _.Region,
                    Lines = _.Lines.Select(l => new LineCountRow(l.Sku, l.Order!.Lines.Count)).ToList()
                })
                .ToScryRequest());

        await Assert.That(exception.Message).Contains("may only carry values read off the element");
    }

    [Test]
    public async Task RowsWithoutASelectAreRefusedByTheClient()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.Throws<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new {_.Region, Lines = _.Lines.ToList()})
                .ToScryRequest());

        await Assert.That(exception.Message).Contains("needs a Select");
    }

    // The client refuses the shape before it is sent, as client-side code inside a nested object; the
    // server refuses the same shape arriving on the wire, by position.
    [Test]
    public async Task ACollectionInsideANestedObjectIsRefused()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        Assert.Throws<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new OrderCard(_.Region, new(_.Lines.Select(l => l.Sku).ToList())))
                .ToScryRequest());

        var request = QueryRequest.Create(
            "OrderLine",
            [
                new SelectOp(new(
                [
                    new("Sku", new NodeValue(new MemberNode(["Sku"]))),
                    new("Order", new NestedValue(
                        ["Order"],
                        new([new("Skus", new CollectionValue(["Lines"], Selector: new MemberNode(["Sku"])))])))
                ]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
        await Assert.That(exception.Message).Contains("top level");
    }

    [Test]
    public async Task ADistinctRowCannotHoldACollection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new SkuRow(_.Region, _.Lines.Select(l => l.Sku).ToList()))
                .Distinct()
                .CountAsync());

        await Assert.That(exception!.Message).Contains("collection projection member");
    }

    [Test]
    public async Task AnElementMayNotAskOfAnotherCollectionOnTheWire()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new(
                [
                    new("Region", new NodeValue(new MemberNode(["Region"]))),
                    new("Lines", new CollectionValue(
                        ["Lines"],
                        Projection: new(
                        [
                            new("Siblings", new NodeValue(new SubqueryNode(["Order", "Lines"], SubqueryFn.Count)))
                        ])))
                ]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
        await Assert.That(exception.Message).Contains("inside another subquery");
    }

    [Test]
    public async Task ACollectionOfValuesHasNoMembersToProject()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new(
                [
                    new("Region", new NodeValue(new MemberNode(["Region"]))),
                    new("Tags", new CollectionValue(
                        ["Tags"],
                        Projection: new([new("Length", new NodeValue(new MemberNode(["Length"])))])))
                ]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
        await Assert.That(exception.Message).Contains("holds values, not rows");
    }

    [Test]
    public Task TheWireRoundTrips()
    {
        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new(
                [
                    new("Region", new NodeValue(new MemberNode(["Region"]))),
                    new("Skus", new CollectionValue(
                        ["Lines"],
                        new BinaryNode(BinaryOp.GreaterThan, new MemberNode(["Quantity"]), new ConstNode("1", ClrTypeTag.Int32)),
                        new MemberNode(["Sku"]))),
                    new("Tags", new CollectionValue(["Tags"], Selector: new ElementNode())),
                    new("Lines", new CollectionValue(
                        ["Lines"],
                        Projection: new(
                        [
                            new("Sku", new NodeValue(new MemberNode(["Sku"]))),
                            new("Price", new NodeValue(new MemberNode(["Price"])))
                        ])))
                ]))
            ]);

        var json = JsonSerializer.Serialize(request, ScryJson.Options);
        var back = ScryJson.DeserializeRequest(json);
        return Verify(new
        {
            json,
            back
        });
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
