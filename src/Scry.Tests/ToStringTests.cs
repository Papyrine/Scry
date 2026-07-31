/// <summary>
/// Reading a value as text. The argument-less <c>ToString()</c> is translated by the provider in every
/// position; the overload taking a format is not translated anywhere, so it is refused rather than
/// shipped — see <see cref="RejectsAFormatSpecifier"/>.
/// </summary>
[TestFixture]
public class ToStringTests
{
    [Test]
    public async Task ReadsANumberAsText()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientToString
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Quantity = _.Quantity.ToString(), Amount = _.Amount.ToString()})
            .ToListAsync();
        // end-snippet

        var row = rows.Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.Quantity, Is.EqualTo("1"));
            Assert.That(row.Amount, Does.StartWith("75"));
        });
    }

    [Test]
    public async Task WorksInAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The position that separates a real translation from client evaluation: a projection would
        // succeed either way, a predicate only if the database does the work.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Quantity.ToString() == "1")
            .Select(_ => new {_.Region})
            .ToListAsync();

        Assert.That(rows.Single().Region, Is.EqualTo("South"));
    }

    [Test]
    public async Task WorksAsAnOrderingKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Quantity.ToString())
            .Select(_ => new {_.Quantity})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Quantity), Is.EqualTo([1u, 3u, 7u]));
    }

    [Test]
    public async Task ComposesWithConcatenation()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new
            {
                Label = $"{_.Region}/{_.Quantity}"
            })
            .ToListAsync();

        Assert.That(rows.Single().Label, Is.EqualTo("South/1"));
    }

    [Test]
    public async Task ReadsADateAsText()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region == "South")
            .Select(_ => new {Placed = _.Placed.ToString()})
            .ToListAsync();

        Assert.That(rows.Single().Placed, Does.Contain("2025"));
    }

    [Test]
    public void RejectsAFormatSpecifier()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Refused at translation, on the client, before a request is sent.
        var exception = Assert.ThrowsAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Select(_ => new {Text = _.Amount.ToString("N2")})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("ToString with a format is not supported"));
    }

    [Test]
    public void RejectsAnInterpolatedFormatSpecifier()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Select(_ => new {Text = $"{_.Amount:N2}"})
                .ToListAsync());

        Assert.That(exception, Is.Not.Null);
    }

    [Test]
    public void RejectsReadingAnEnumAsText()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // An enum's text is a member name the database does not hold — the column carries the
        // underlying value — so converting one in SQL would answer with a number.
        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Employee>("Employee")
                .Select(_ => new {Text = _.Status.ToString()})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("not supported over an enum"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
