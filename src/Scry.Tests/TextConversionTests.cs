/// <summary>
/// The parsing functions — <c>int.Parse</c> / <c>Convert.To*</c> carried as <c>Int32From</c> and its
/// siblings. Only the text-to-value direction exists: a numeric member is already a value, and SQL's
/// numeric-to-numeric conversions truncate where the CLR's round, so that direction is refused rather
/// than answered differently per source.
/// </summary>
[TestFixture]
public class TextConversionTests
{
    [Test]
    public async Task ReadsTextAsANumberInAProjection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Code, Value = int.Parse(_.Code)})
            .ToListAsync();

        Assert.That(
            rows.OrderBy(_ => _.Value).Select(_ => (_.Code, _.Value)),
            Is.EqualTo([("8", 8), ("17", 17), ("40", 40)]));
    }

    // Numeric order and string order disagree over the seeded codes — "8" sorts after "40" as text —
    // so this passing means the ordering ran over the parsed value, in the database.
    [Test]
    public async Task OrdersByTheParsedValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => int.Parse(_.Code))
            .Select(_ => new {_.Code})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Code), Is.EqualTo(["8", "17", "40"]));
    }

    [Test]
    public async Task FiltersByTheParsedValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order")
            .CountAsync(_ => long.Parse(_.Code) > 10);

        Assert.That(count, Is.EqualTo(2));
    }

    // The Convert spellings reach the same functions as Parse, and Convert.ToString is StringFrom by
    // another name.
    [Test]
    public async Task ConvertSpellingsMeanTheSame()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Code == "40")
            .Select(_ => new
            {
                Int = Convert.ToInt32(_.Code),
                Long = Convert.ToInt64(_.Code),
                Decimal = Convert.ToDecimal(_.Code),
                Double = Convert.ToDouble(_.Code),
                Text = Convert.ToString(_.Quantity)
            })
            .ToListAsync();

        var row = rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Int, Is.EqualTo(40));
            Assert.That(row.Long, Is.EqualTo(40L));
            Assert.That(row.Decimal, Is.EqualTo(40m));
            Assert.That(row.Double, Is.EqualTo(40d));
            Assert.That(row.Text, Is.EqualTo("3"));
        });
    }

    [Test]
    public void ANumericMemberIsRefusedAtTranslation()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new {Value = Convert.ToInt32(_.Amount)})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("already one"));
    }

    // The same refusal server-side, for a request that did not come through the translator.
    [Test]
    public void ANumericMemberIsRefusedByTheServer()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.GreaterThan,
                        new CallNode(KnownFunction.Int32From, new MemberNode(["Amount"]), []),
                        new ConstNode("10", ClrTypeTag.Int32))),
                new CountOp()
            ]);

        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("reads text as a value"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
