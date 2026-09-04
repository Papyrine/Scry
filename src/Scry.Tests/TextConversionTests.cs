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
    public async Task ParsesTheRemainingNumericTargets()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Code == "40")
            .Select(_ => new
            {
                Byte = byte.Parse(_.Code),
                Short = short.Parse(_.Code),
                Float = float.Parse(_.Code),
                ByteAgain = Convert.ToByte(_.Code),
                ShortAgain = Convert.ToInt16(_.Code)
            })
            .ToListAsync();

        var row = rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Byte, Is.EqualTo((byte)40));
            Assert.That(row.Short, Is.EqualTo((short)40));
            Assert.That(row.Float, Is.EqualTo(40f));
            Assert.That(row.ByteAgain, Is.EqualTo((byte)40));
            Assert.That(row.ShortAgain, Is.EqualTo((short)40));
        });
    }

    [Test]
    public async Task ParsesBooleanText()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var parsed = await client.Source<Order>("Order").CountAsync(_ => bool.Parse(_.Audited));
        var converted = await client.Source<Order>("Order").CountAsync(_ => Convert.ToBoolean(_.Audited));

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.EqualTo(2));
            Assert.That(converted, Is.EqualTo(2));
        });
    }

    // ToSingle is the one Convert spelling deliberately left out: the provider translates float.Parse
    // but carries no ToSingle conversion, so the spelling would trade a translation-time refusal for
    // an execution fault.
    [Test]
    public void ConvertToSingleStaysClientSide()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new {Value = Convert.ToSingle(_.Code)})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("client-side"));
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
    public void ANarrowingOfANumericMemberIsRefusedByTheServer()
    {
        // Over a number the function is a cast, and only a widening one is carried: reading a decimal
        // as an int would truncate in the database where the CLR rounds.
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

        Assert.That(exception!.Message, Does.Contain("would narrow"));
    }

    [Test]
    public void AWideningOfANumericMemberIsACast()
    {
        // The same function over a narrower member is the cast a client writes as (double)_.Quantity.
        // Quantities are 3, 7 and 1, so two are above 2.5 once compared as doubles.
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.GreaterThan,
                        new CallNode(KnownFunction.DoubleFrom, new MemberNode(["Quantity"]), []),
                        new ConstNode("2.5", ClrTypeTag.Double))),
                new CountOp()
            ]);

        var response = SharedProcessor.Instance.Execute(request, context);

        Assert.That(response.Payload.GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public void AValueThatIsNeitherTextNorANumberIsRefusedByTheServer()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.GreaterThan,
                        new CallNode(KnownFunction.Int32From, new MemberNode(["Placed"]), []),
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
