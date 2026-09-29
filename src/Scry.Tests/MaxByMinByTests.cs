/// <summary>
/// <c>MaxByAsync</c> / <c>MinByAsync</c> — the <c>OrderBy</c> + <c>First</c> they abbreviate, the
/// same unfolding EF applies to <c>Queryable.MaxBy</c> / <c>MinBy</c>. The ordering precedes any
/// projection, so the key reads the row and the answer is the row itself, default-projected.
/// </summary>
public class MaxByMinByTests
{
    [Test]
    public async Task ReturnsTheRowCarryingTheGreatestKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var top = await client.Source<Order>("Order").MaxByAsync(_ => _.Amount);

        using (Assert.Multiple())
        {
            await Assert.That(top!.Amount).IsEqualTo(250m);
            await Assert.That(top.Region).IsEqualTo("North");
        }
    }

    [Test]
    public async Task ReturnsTheRowCarryingTheLeastKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var bottom = await client.Source<Order>("Order").MinByAsync(_ => _.Amount);

        await Assert.That(bottom!.Region).IsEqualTo("South");
    }

    [Test]
    public async Task ComposesWithAFilter()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var cheapest = await client.Source<Order>("Order")
            .Where(_ => _.Region == "North")
            .MinByAsync(_ => _.Amount);

        await Assert.That(cheapest!.Amount).IsEqualTo(100m);
    }

    [Test]
    public async Task OrdersByADateKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var latest = await client.Source<Order>("Order").MaxByAsync(_ => _.Placed);

        await Assert.That(latest!.Placed).IsEqualTo(new(2026, 7, 20, 14, 5, 0));
    }

    [Test]
    public async Task TheOrDefaultFormAnswersAnEmptyQueryWithNull()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var missing = await client.Source<Order>("Order")
            .Where(_ => _.Region == "West")
            .MaxByOrDefaultAsync(_ => _.Amount);

        await Assert.That(missing).IsNull();
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
