/// <summary>
/// The parsing functions — <c>int.Parse</c> / <c>Convert.To*</c> carried as <c>Int32From</c> and its
/// siblings. Only the text-to-value direction exists: a numeric member is already a value, and SQL's
/// numeric-to-numeric conversions truncate where the CLR's round, so that direction is refused rather
/// than answered differently per source.
/// </summary>
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

        await Assert.That(rows.OrderBy(_ => _.Value).Select(_ => (_.Code, _.Value))).IsEquivalentTo([("8", 8), ("17", 17), ("40", 40)], CollectionOrdering.Matching);
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

        await Assert.That(rows.Select(_ => _.Code)).IsEquivalentTo(["8", "17", "40"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FiltersByTheParsedValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order")
            .CountAsync(_ => long.Parse(_.Code) > 10);

        await Assert.That(count).IsEqualTo(2);
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
        using (Assert.Multiple())
        {
            await Assert.That(row.Int).IsEqualTo(40);
            await Assert.That(row.Long).IsEqualTo(40L);
            await Assert.That(row.Decimal).IsEqualTo(40m);
            await Assert.That(row.Double).IsEqualTo(40d);
            await Assert.That(row.Text).IsEqualTo("3");
        }
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
        using (Assert.Multiple())
        {
            await Assert.That(row.Byte).IsEqualTo((byte)40);
            await Assert.That(row.Short).IsEqualTo((short)40);
            await Assert.That(row.Float).IsEqualTo(40f);
            await Assert.That(row.ByteAgain).IsEqualTo((byte)40);
            await Assert.That(row.ShortAgain).IsEqualTo((short)40);
        }
    }

    [Test]
    public async Task ParsesBooleanText()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var parsed = await client.Source<Order>("Order").CountAsync(_ => bool.Parse(_.Audited));
        var converted = await client.Source<Order>("Order").CountAsync(_ => Convert.ToBoolean(_.Audited));

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsEqualTo(2);
            await Assert.That(converted).IsEqualTo(2);
        }
    }

    // ToSingle is the one Convert spelling deliberately left out: the provider translates float.Parse
    // but carries no ToSingle conversion, so the spelling would trade a translation-time refusal for
    // an execution fault.
    [Test]
    public async Task ConvertToSingleStaysClientSide()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new {Value = Convert.ToSingle(_.Code)})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("client-side");
    }

    [Test]
    public async Task ANumericMemberIsRefusedAtTranslation()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .Select(_ => new {Value = Convert.ToInt32(_.Amount)})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("already one");
    }

    // The same refusal server-side, for a request that did not come through the translator.
    [Test]
    public async Task ANarrowingOfANumericMemberIsRefusedByTheServer()
    {
        // Over a number the function is a cast, and only a widening one is carried: reading a decimal
        // as an int would truncate in the database where the CLR rounds.
        await using var context = TestContext.CreateSeeded();

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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("would narrow");
    }

    [Test]
    public async Task AWideningOfANumericMemberIsACast()
    {
        // The same function over a narrower member is the cast a client writes as (double)_.Quantity.
        // Quantities are 3, 7 and 1, so two are above 2.5 once compared as doubles.
        await using var context = TestContext.CreateSeeded();

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

        await Assert.That(response.Payload.GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task AValueThatIsNeitherTextNorANumberIsRefusedByTheServer()
    {
        await using var context = TestContext.CreateSeeded();

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

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("reads text as a value");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
