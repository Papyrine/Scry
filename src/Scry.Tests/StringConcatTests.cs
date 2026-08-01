/// <summary>
/// String concatenation where an operand is not a string. C# writes it as <c>+</c>, but the operator
/// alone cannot say which was meant — an Add of a string and a number is a concatenation, an Add of
/// two numbers is arithmetic — so the client records the intent while the compiler's method is still
/// visible, rather than leaving the server to guess from operand types.
/// </summary>
[TestFixture]
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

        Assert.That(rows.Select(_ => _.Label).Order(), Is.EqualTo(["North-3", "North-7", "South-1"]));
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

        Assert.That(rows.Select(_ => _.Label).Order(), Is.EqualTo(["North:2", "South:1"]));
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

        Assert.That(rows.Single().Label, Is.EqualTo("Qty 1"));
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

        Assert.Multiple(() =>
        {
            Assert.That(row.Amount, Does.StartWith("South").And.Contains("75"));
            Assert.That(row.Year, Is.EqualTo("South2025"));
        });
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

        Assert.That(rows.Single().Label, Is.EqualTo("South!"));
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

        Assert.Multiple(() =>
        {
            Assert.That(row.Sum, Is.EqualTo(3));
            Assert.That(row.Amount, Is.EqualTo(77m));
        });
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

        Assert.That(rows.Single().Region, Is.EqualTo("South"));
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

        Assert.That(rows.First().Region, Is.EqualTo("South"));
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

        Assert.That(rows.Single().Label, Is.EqualTo("South/1"));
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

        Assert.That(rows.Single().Label, Is.EqualTo("South1x"));
    }

    [Test]
    public void StillRejectsAFormattedHole()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A format specifier would change the value, and the database has no equivalent spelling.
        var exception = Assert.ThrowsAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Select(_ => new {Label = $"{_.Amount:N2}"})
                .ToListAsync());

        Assert.That(exception, Is.Not.Null);
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
