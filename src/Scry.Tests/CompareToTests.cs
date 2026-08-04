/// <summary>
/// The three-way comparison — <c>a.CompareTo(b)</c> and <c>string.Compare(a, b)</c>, carried as
/// <c>CompareTo</c>. The server emits the CLR call and EF owns the SQL, a CASE over the two operands;
/// text compares under the server's collation, exactly as ordering does.
/// </summary>
[TestFixture]
public class CompareToTests
{
    [Test]
    public async Task ComparesANumberThreeWays()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Amount, Cmp = _.Amount.CompareTo(100m)})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single(_ => _.Amount == 75m).Cmp, Is.EqualTo(-1));
            Assert.That(rows.Single(_ => _.Amount == 100m).Cmp, Is.Zero);
            Assert.That(rows.Single(_ => _.Amount == 250m).Cmp, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ComparesTextUnderTheServersCollation()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order")
            .CountAsync(_ => _.Region.CompareTo("South") < 0);

        Assert.That(count, Is.EqualTo(2));
    }

    // The static spelling means the same as the instance one.
    [Test]
    public async Task StaticCompareMeansTheSame()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Select(_ => new {_.Name, Cmp = string.Compare(_.Name, "Bob")})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single(_ => _.Name == "Bob").Cmp, Is.Zero);
            Assert.That(rows.Single(_ => _.Name == "Alice").Cmp, Is.EqualTo(-1));
            Assert.That(rows.Single(_ => _.Name == "Carol").Cmp, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ComparesADate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // One order is placed in 2025; the cutoff splits it from the other two.
        var cutoff = new DateTime(2026, 1, 1);
        var count = await client.Source<Order>("Order")
            .CountAsync(_ => _.Placed.CompareTo(cutoff) < 0);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task ComparesAnUnsignedMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Quantities are 3, 7 and 1.
        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Quantity, Cmp = _.Quantity.CompareTo(3u)})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Cmp).Order(), Is.EqualTo([-1, 0, 1]));
    }

    // A null operand keeps the answer null: a comparison against a value that is not there has no
    // direction.
    [Test]
    public void KeepsNullNullRatherThanPickingADirection()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(
                    new(
                    [
                        new("Discount", new NodeValue(new MemberNode(["Discount"]))),
                        new("Cmp", new NodeValue(new CallNode(KnownFunction.CompareTo, new MemberNode(["Discount"]), [new ConstNode("7", ClrTypeTag.Decimal)])))
                    ]))
            ]);

        var response = SharedProcessor.Instance.Execute(request, context);
        var rows = response.Payload.EnumerateArray().ToList();

        Assert.Multiple(() =>
        {
            var absent = rows.Single(_ => _.GetProperty("discount").ValueKind == JsonValueKind.Null);
            Assert.That(absent.GetProperty("cmp").ValueKind, Is.EqualTo(JsonValueKind.Null));

            var compared = rows
                .Where(_ => _.GetProperty("discount").ValueKind != JsonValueKind.Null)
                .Select(_ => _.GetProperty("cmp").GetInt32());
            Assert.That(compared.Order(), Is.EqualTo([-1, 1]));
        });
    }

    [Test]
    public void RejectsSomethingWithoutAnOrdering()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Cmp", new NodeValue(new CallNode(KnownFunction.CompareTo, new MemberNode(["Active"]), [new ConstNode("true", ClrTypeTag.Boolean)])))]))]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("CompareTo is not supported over"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
