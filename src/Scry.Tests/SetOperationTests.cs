/// <summary>
/// Two sources combined into one sequence. Each is resolved and policy-filtered before they meet, and
/// both must project the same shape — a combined row carries no record of which side produced it.
/// </summary>
[TestFixture]
public class SetOperationTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record Label(string Name, decimal Value);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task Union()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientUnion
        var rows = await client.Source<Order>("Order")
            .Select(_ => new Label(_.Region, _.Amount))
            .Union(client.Source<OrderLine>("OrderLine")
                .Select(_ => new Label(_.Sku, _.Price)))
            .ToListAsync();
        // end-snippet

        // Three orders and three lines, none of them equal as a pair.
        Assert.That(rows, Has.Count.EqualTo(6));
    }

    [Test]
    public async Task UnionDeduplicatesAcrossTheSides()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Both sides project the same constant-ish shape from the same source, so Union collapses the
        // duplicates that Concat keeps.
        var union = await client.Source<Order>("Order")
            .Select(_ => new Label(_.Region, _.Amount))
            .Union(client.Source<Order>("Order").Select(_ => new Label(_.Region, _.Amount)))
            .ToListAsync();

        var concat = await client.Source<Order>("Order")
            .Select(_ => new Label(_.Region, _.Amount))
            .Concat(client.Source<Order>("Order").Select(_ => new Label(_.Region, _.Amount)))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(union, Has.Count.EqualTo(3));
            Assert.That(concat, Has.Count.EqualTo(6));
        });
    }

    [Test]
    public async Task IntersectAndExcept()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var intersect = await client.Source<Order>("Order")
            .Select(_ => new Label(_.Region, _.Amount))
            .Intersect(client.Source<Order>("Order")
                .Where(_ => _.Region == "North")
                .Select(_ => new Label(_.Region, _.Amount)))
            .ToListAsync();

        var except = await client.Source<Order>("Order")
            .Select(_ => new Label(_.Region, _.Amount))
            .Except(client.Source<Order>("Order")
                .Where(_ => _.Region == "North")
                .Select(_ => new Label(_.Region, _.Amount)))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(intersect, Has.Count.EqualTo(2));
            Assert.That(except.Single().Name, Is.EqualTo("South"));
        });
    }

    [Test]
    public async Task CountOverASetOperation()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order")
            .Select(_ => new Label(_.Region, _.Amount))
            .Union(client.Source<OrderLine>("OrderLine").Select(_ => new Label(_.Sku, _.Price)))
            .CountAsync();

        Assert.That(count, Is.EqualTo(6));
    }

    [Test]
    public async Task TheOtherSourcePolicyIsAppliedBeforeCombining()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Ticket carries [ReturnableWith(OpenTicketsOnlyPolicy)], hiding the closed one. Combining must
        // not become a way to read it.
        var rows = await client.Source<Department>("Department")
            .Select(_ => new Label(_.Name, _.Id))
            .Union(client.Source<Ticket>("Ticket").Select(_ => new Label(_.Name, _.Id)))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(_ => _.Name), Does.Not.Contain("Old typo"));
            Assert.That(rows, Has.Count.EqualTo(4), "two departments and the two open tickets");
        });
    }

    [Test]
    public void MismatchedMemberNamesAreRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])),
                new SetOp(
                    SetKind.Union,
                    "OrderLine",
                    null,
                    new([new("Sku", new NodeValue(new MemberNode(["Sku"])))]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("same members"));
    }

    [Test]
    public void MismatchedMemberTypesAreRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new([new("Value", new NodeValue(new MemberNode(["Region"])))])),
                new SetOp(
                    SetKind.Union,
                    "OrderLine",
                    null,
                    new([new("Value", new NodeValue(new MemberNode(["Price"])))]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("same types"));
    }

    [Test]
    public void AnIgnoredMemberStaysHiddenOnTheOtherSide()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new([new("Value", new NodeValue(new MemberNode(["Amount"])))])),
                new SetOp(
                    SetKind.Union,
                    "Employee",
                    null,
                    new([new("Value", new NodeValue(new MemberNode(["Salary"])))]))
            ]);

        Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void OperatorsAfterASetOperationAreRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Order>("Order")
                .Select(_ => new Label(_.Region, _.Amount))
                .Union(client.Source<OrderLine>("OrderLine").Select(_ => new Label(_.Sku, _.Price)))
                .OrderBy(_ => _.Name)
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("may follow a set operation"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
