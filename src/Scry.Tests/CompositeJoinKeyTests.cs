/// <summary>
/// Composite join keys — <c>new {_.A, _.B}</c> on both sides, compared part by part. The wire
/// carries a <see cref="CompositeKeyNode"/> in the ordinary key slots, so a server predating it
/// rejects the request at deserialization rather than joining on less than the whole key; the server
/// builds one <see cref="DistinctRow"/> per side, whose member-wise equality the provider decomposes
/// into per-part comparisons.
/// </summary>
public class CompositeJoinKeyTests
{
    // Self-joining Order on {Region, Grade} pairs each row with itself alone. Region by itself would
    // also pair the two North rows across grades — the single-key test below pins that difference,
    // which is what proves both parts took part.
    [Test]
    public async Task JoinsOnEveryPartAtOnce()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Join(
                client.Source<Order>("Order"),
                _ => new {_.Region, _.Grade},
                _ => new {_.Region, _.Grade},
                (outer, inner) => new {outer.Code, Matched = inner.Amount})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(rows).Count().IsEqualTo(3);
            await Assert.That(rows.Single(_ => _.Code == "40").Matched).IsEqualTo(100m);
            await Assert.That(rows.Single(_ => _.Code == "8").Matched).IsEqualTo(250m);
            await Assert.That(rows.Single(_ => _.Code == "17").Matched).IsEqualTo(75m);
        }
    }

    [Test]
    public async Task ASingleKeyPairsMoreRows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Join(
                client.Source<Order>("Order"),
                _ => _.Region,
                _ => _.Region,
                (outer, inner) => new {outer.Code, Matched = inner.Amount})
            .ToListAsync();

        // Both North rows pair with both North rows.
        await Assert.That(rows).Count().IsEqualTo(5);
    }

    [Test]
    public async Task GroupJoinsOnACompositeKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupJoin(
                client.Source<Order>("Order"),
                _ => new {_.Region, _.Grade},
                _ => new {_.Region, _.Grade},
                (outer, twins) => new {outer.Code, Twins = twins.Count()})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Twins)).All(_ => Equals(_, 1));
    }

    [Test]
    public async Task ACompositeOnOneSideAloneIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new JoinOp(
                    "Order",
                    JoinKind.Inner,
                    new CompositeKeyNode([new MemberNode(["Region"]), new MemberNode(["Grade"])]),
                    new MemberNode(["Region"]),
                    null,
                    [new("Code", JoinSide.Outer, ["Code"])])
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("composite on both sides");
    }

    [Test]
    public async Task MismatchedPartCountsAreRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new JoinOp(
                    "Order",
                    JoinKind.Inner,
                    new CompositeKeyNode([new MemberNode(["Region"]), new MemberNode(["Grade"])]),
                    new CompositeKeyNode([new MemberNode(["Region"])]),
                    null,
                    [new("Code", JoinSide.Outer, ["Code"])])
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("pairs its parts");
    }

    // A composite has no value of its own, so anywhere a value is expected it is an unsupported
    // expression.
    [Test]
    public async Task ACompositeKeyOutsideAJoinIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new CompositeKeyNode([new MemberNode(["Region"]), new MemberNode(["Grade"])]),
                        new ConstNode("x", ClrTypeTag.String))),
                new CountOp()
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Unsupported expression");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
