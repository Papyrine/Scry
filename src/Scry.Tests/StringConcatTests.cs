/// <summary>
/// String concatenation where an operand is not a string. C# writes it as <c>+</c>, but the operator
/// alone cannot say which was meant — an Add of a string and a number is a concatenation, an Add of
/// two numbers is arithmetic — so the client records the intent while the compiler's method is still
/// visible, rather than leaving the server to guess from operand types.
/// </summary>
public class StringConcatTests
{
    [Test]
    public async Task ConcatenatesAStringMemberWithANumericOne()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientStringConcat
        var rows = await client.Source<Order>("Order")
            .Select(_ => new {Label = $"{_.Region}-{_.Quantity}"})
            .ToListAsync();
        // end-snippet

        await Assert.That(rows.Select(_ => _.Label).Order()).IsEquivalentTo(["North-3", "North-7", "South-1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConcatenatesAGroupKeyWithAnAggregate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new {Label = $"{_.Key}:{_.Count()}"})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Label).Order()).IsEquivalentTo(["North:2", "South:1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task StartsWithALiteral()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A leading literal leaves the non-string operand on the right, which the operator alone would
        // have read as an attempt at arithmetic against a string constant.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Label = $"Qty {_.Quantity}"})
            .ToListAsync();

        await Assert.That(rows.Single().Label).IsEqualTo("Qty 1");
    }

    [Test]
    public async Task ConcatenatesADecimalAndADate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Amount = _.Region + _.Amount, Year = _.Region + _.Placed.Year})
            .ToListAsync();

        var row = rows.Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.Amount).StartsWith("South").And.Contains("75");
            await Assert.That(row.Year).IsEqualTo("South2025");
        }
    }

    [Test]
    public async Task StillConcatenatesTwoStrings()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Label = _.Region + "!"})
            .ToListAsync();

        await Assert.That(rows.Single().Label).IsEqualTo("South!");
    }

    [Test]
    public async Task ArithmeticIsStillArithmetic()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The same operator over two numbers still adds them rather than joining their text.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Sum = _.Quantity + 2, Amount = _.Amount + 2})
            .ToListAsync();

        var row = rows.Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.Sum).IsEqualTo(3u);
            await Assert.That(row.Amount).IsEqualTo(77m);
        }
    }

    [Test]
    public async Task ConcatenatesInAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region + _.Quantity == "South1")
            .Select(_ => new {_.Region})
            .ToListAsync();

        await Assert.That(rows.Single().Region).IsEqualTo("South");
    }

    [Test]
    public async Task ConcatenatesInAnOrderingKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderByDescending(_ => _.Region + _.Quantity)
            .Select(_ => new {_.Region, _.Quantity})
            .ToListAsync();

        await Assert.That(rows.First().Region).IsEqualTo("South");
    }

    [Test]
    public async Task InterpolatesANonStringHole()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // An interpolated string is rewritten into the concatenation it is equivalent to, so a hole
        // that is not a string is now as workable there as it is with '+'.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Label = $"{_.Region}/{_.Quantity}"})
            .ToListAsync();

        await Assert.That(rows.Single().Label).IsEqualTo("South/1");
    }

    [Test]
    public async Task ConcatenatesThroughStringConcat()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Label = string.Concat(_.Region, _.Quantity, "x")})
            .ToListAsync();

        await Assert.That(rows.Single().Label).IsEqualTo("South1x");
    }

    [Test]
    public async Task StillRejectsAFormattedHole()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A format specifier would change the value, and the database has no equivalent spelling.
        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Select(_ => new {Label = $"{_.Amount:N2}"})
                .ToListAsync());

        await Assert.That(exception).IsNotNull();
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
