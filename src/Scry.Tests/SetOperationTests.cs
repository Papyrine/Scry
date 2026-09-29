/// <summary>
/// Two sources combined into one sequence. Each is resolved and policy-filtered before they meet, and
/// both must project the same shape — a combined row carries no record of which side produced it.
/// </summary>
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
        await Assert.That(rows).Count().IsEqualTo(6);
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

        using (Assert.Multiple())
        {
            await Assert.That(union).Count().IsEqualTo(3);
            await Assert.That(concat).Count().IsEqualTo(6);
        }
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

        using (Assert.Multiple())
        {
            await Assert.That(intersect).Count().IsEqualTo(2);
            await Assert.That(except.Single().Name).IsEqualTo("South");
        }
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

        await Assert.That(count).IsEqualTo(6);
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

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(_ => _.Name)).DoesNotContain("Old typo");
            await Assert.That(rows).Count().IsEqualTo(4).Because("two departments and the two open tickets");
        }
    }

    [Test]
    public async Task MismatchedMemberNamesAreRejected()
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("same members");
    }

    [Test]
    public async Task MismatchedMemberTypesAreRejected()
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("same types");
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

        Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public async Task OperatorsAfterASetOperationAreRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => client.Source<Order>("Order")
                .Select(_ => new Label(_.Region, _.Amount))
                .Union(client.Source<OrderLine>("OrderLine").Select(_ => new Label(_.Sku, _.Price)))
                .OrderBy(_ => _.Name)
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("may follow a set operation");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
