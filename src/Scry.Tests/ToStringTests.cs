/// <summary>
/// Reading a value as text. The argument-less <c>ToString()</c> is translated by the provider in every
/// position; the overload taking a format is not translated anywhere, so it is refused rather than
/// shipped — see <see cref="RejectsAFormatSpecifier"/>.
/// </summary>
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
            .Select(_ => new
            {
                Quantity = _.Quantity.ToString(),
                Amount = _.Amount.ToString()
            })
            .ToListAsync();
        // end-snippet

        var row = rows.Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.Quantity).IsEqualTo("1");
            await Assert.That(row.Amount).StartsWith("75");
        }
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

        await Assert.That(rows.Single().Region).IsEqualTo("South");
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

        await Assert.That(rows.Select(_ => _.Quantity)).IsEquivalentTo([1u, 3u, 7u], CollectionOrdering.Matching);
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

        await Assert.That(rows.Single().Label).IsEqualTo("South/1");
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

        await Assert.That(rows.Single().Placed).Contains("2025");
    }

    [Test]
    public async Task RejectsAFormatSpecifier()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Refused at translation, on the client, before a request is sent.
        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Select(_ => new {Text = _.Amount.ToString("N2")})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("ToString with a format is not supported");
    }

    [Test]
    public async Task RejectsAnInterpolatedFormatSpecifier()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .Select(_ => new {Text = $"{_.Amount:N2}"})
                .ToListAsync());

        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task RejectsReadingAnEnumAsText()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // An enum's text is a member name the database does not hold — the column carries the
        // underlying value — so converting one in SQL would answer with a number.
        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => client.Source<Employee>("Employee")
                .Select(_ => new {Text = _.Status.ToString()})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("not supported over an enum");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
