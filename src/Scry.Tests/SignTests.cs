/// <summary>
/// <c>Math.Sign</c>. The provider translates it, but SQL's <c>SIGN</c> returns its argument's type
/// while the CLR method returns an int, so its result cannot be read back — the query succeeds in a
/// predicate and faults in a projection. The server composes the same answer from comparisons
/// instead, which any relational provider translates and which yields an int by construction.
/// </summary>
public class SignTests
{
    [Test]
    public async Task ReadsTheSignInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Amounts are 100, 250 and 75, so this covers all three answers without needing rows the
        // shared read-only seed does not have.
        // begin-snippet: clientSign
        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Amount, Sign = Math.Sign(_.Amount - 100m)})
            .ToListAsync();
        // end-snippet

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single(_ => _.Amount == 100m).Sign).IsZero();
            await Assert.That(rows.Single(_ => _.Amount == 250m).Sign).IsEqualTo(1);
            await Assert.That(rows.Single(_ => _.Amount == 75m).Sign).IsEqualTo(-1);
        }
    }

    [Test]
    public async Task ReadsTheSignOfAnIntegerMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Quantity, Sign = Math.Sign((int)_.Quantity - 3)})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single(_ => _.Quantity == 1).Sign).IsEqualTo(-1);
            await Assert.That(rows.Single(_ => _.Quantity == 3).Sign).IsZero();
            await Assert.That(rows.Single(_ => _.Quantity == 7).Sign).IsEqualTo(1);
        }
    }

    [Test]
    public async Task FiltersByTheSign()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => Math.Sign(_.Amount - 100m) < 0)
            .Select(_ => new {_.Amount})
            .ToListAsync();

        await Assert.That(rows.Single().Amount).IsEqualTo(75m);
    }

    [Test]
    public async Task OrdersByTheSign()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => Math.Sign(_.Amount - 100m))
            .Select(_ => new {_.Amount})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Amount)).IsEquivalentTo([75m, 100m, 250m], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadsTheSignOfADouble()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Amount == 75m)
            .Select(_ => new {Sign = Math.Sign(_.Quantity - 5d)})
            .ToListAsync();

        await Assert.That(rows.Single().Sign).IsEqualTo(-1);
    }

    [Test]
    public async Task KeepsNullNullRatherThanCallingItZero()
    {
        using var context = TestContext.CreateSeeded();

        // Discount is null on one row. A comparison against null is neither greater nor less, so an
        // unguarded chain would answer zero — the sign of a value that is not there.
        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(
                    new(
                    [
                        new("Discount", new NodeValue(new MemberNode(["Discount"]))),
                        new("Sign", new NodeValue(new CallNode(KnownFunction.MathSign, new MemberNode(["Discount"]), [])))
                    ]))
            ]);

        var response = SharedProcessor.Instance.Execute(request, context);
        var rows = response.Payload.EnumerateArray().ToList();

        using (Assert.Multiple())
        {
            var absent = rows.Single(_ => _.GetProperty("discount").ValueKind == JsonValueKind.Null);
            await Assert.That(absent.GetProperty("sign").ValueKind).IsEqualTo(JsonValueKind.Null);

            var present = rows.Where(_ => _.GetProperty("discount").ValueKind != JsonValueKind.Null);
            await Assert.That(present.Select(_ => _.GetProperty("sign").GetInt32())).All(_ => Equals(_, 1));
        }
    }

    [Test]
    public async Task RejectsTheSignOfSomethingNotNumeric()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Sign", new NodeValue(new CallNode(KnownFunction.MathSign, new MemberNode(["Region"]), [])))]))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception!.Message).Contains("Sign is not supported over");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
