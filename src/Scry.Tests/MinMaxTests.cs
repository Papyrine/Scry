/// <summary>
/// <c>Math.Max</c> / <c>Math.Min</c>, carried as <c>MathMax</c> / <c>MathMin</c>. The server composes
/// each from a comparison rather than using SQL's <c>GREATEST</c> / <c>LEAST</c>, which exist only
/// from SQL Server 2022 — a conditional says the same thing on any provider, and a null operand keeps
/// the answer null where GREATEST would skip it.
/// </summary>
public class MinMaxTests
{
    [Test]
    public async Task ReadsTheGreaterAndLesserInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Amount, Floored = Math.Max(_.Amount, 100m), Capped = Math.Min(_.Amount, 100m)})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single(_ => _.Amount == 75m).Floored).IsEqualTo(100m);
            await Assert.That(rows.Single(_ => _.Amount == 75m).Capped).IsEqualTo(75m);
            await Assert.That(rows.Single(_ => _.Amount == 250m).Floored).IsEqualTo(250m);
            await Assert.That(rows.Single(_ => _.Amount == 250m).Capped).IsEqualTo(100m);
        }
    }

    [Test]
    public async Task ComparesTwoRowMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Discounts are 10, null and 5; the coalesce keeps both operands non-null, so the answer is
        // the plain greater of the two.
        var count = await client.Source<Order>("Order")
            .CountAsync(_ => Math.Max(_.Amount, _.Discount.GetValueOrDefault()) > 90);

        await Assert.That(count).IsEqualTo(2);
    }

    // Max(Max(a, b), c) is how C# spells a three-way greatest, and each call composes independently.
    [Test]
    public async Task NestsIntoAThreeWayGreatest()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {Widest = Math.Max(Math.Max(_.Amount, 90m), 120m)})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Widest).Order()).IsEquivalentTo([120m, 120m, 250m], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WorksOverIntegerMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Quantities are 3, 7 and 1.
        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Quantity, AtLeast = Math.Max(_.Quantity, 2u)})
            .ToListAsync();

        await Assert.That(rows.Single(_ => _.Quantity == 1u).AtLeast).IsEqualTo(2u);
    }

    // A null operand keeps the answer null. GREATEST would skip the null and answer with the other
    // operand — the greater of one value, not of two — and an unguarded CASE would do the same.
    [Test]
    public async Task KeepsNullNullRatherThanAnsweringTheOtherOperand()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(
                    new(
                    [
                        new("Discount", new NodeValue(new MemberNode(["Discount"]))),
                        new("Widest", new NodeValue(new CallNode(KnownFunction.MathMax, new MemberNode(["Discount"]), [new ConstNode("7", ClrTypeTag.Decimal)])))
                    ]))
            ]);

        var response = SharedProcessor.Instance.Execute(request, context);
        var rows = response.Payload.EnumerateArray().ToList();

        using (Assert.Multiple())
        {
            var absent = rows.Single(_ => _.GetProperty("discount").ValueKind == JsonValueKind.Null);
            await Assert.That(absent.GetProperty("widest").ValueKind).IsEqualTo(JsonValueKind.Null);

            var widest = rows
                .Where(_ => _.GetProperty("discount").ValueKind != JsonValueKind.Null)
                .Select(_ => _.GetProperty("widest").GetDecimal());
            await Assert.That(widest.Order()).IsEquivalentTo([7m, 10m], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task RejectsSomethingNotNumeric()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Widest", new NodeValue(new CallNode(KnownFunction.MathMax, new MemberNode(["Region"]), [new ConstNode("x", ClrTypeTag.String)])))]))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("Max is not supported over");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
