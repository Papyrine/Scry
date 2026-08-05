/// <summary>
/// Composite join keys — <c>new {_.A, _.B}</c> on both sides, compared part by part. The wire
/// carries a <see cref="CompositeKeyNode"/> in the ordinary key slots, so a server predating it
/// rejects the request at deserialization rather than joining on less than the whole key; the server
/// builds one <see cref="DistinctRow"/> per side, whose member-wise equality the provider decomposes
/// into per-part comparisons.
/// </summary>
[TestFixture]
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

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(rows.Single(_ => _.Code == "40").Matched, Is.EqualTo(100m));
            Assert.That(rows.Single(_ => _.Code == "8").Matched, Is.EqualTo(250m));
            Assert.That(rows.Single(_ => _.Code == "17").Matched, Is.EqualTo(75m));
        });
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
        Assert.That(rows, Has.Count.EqualTo(5));
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

        Assert.That(rows.Select(_ => _.Twins), Is.All.EqualTo(1));
    }

    [Test]
    public void ACompositeOnOneSideAloneIsRejected()
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

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("composite on both sides"));
    }

    [Test]
    public void MismatchedPartCountsAreRejected()
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

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("pairs its parts"));
    }

    // A composite has no value of its own, so anywhere a value is expected it is an unsupported
    // expression.
    [Test]
    public void ACompositeKeyOutsideAJoinIsRejected()
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

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Unsupported expression"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
