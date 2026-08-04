/// <summary>
/// <c>GroupBy(key, (key, group) =&gt; …)</c> — sugar the client unfolds into the <c>GroupBy</c> +
/// <c>Select</c> it abbreviates, so the wire carries the same two operators either way.
/// </summary>
[TestFixture]
public class GroupByResultSelectorTests
{
    [Test]
    public async Task FoldsEachGroupThroughTheResultSelector()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region, (region, orders) => new {Region = region, Total = orders.Sum(_ => _.Amount), Rows = orders.Count()})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            var north = regions.Single(_ => _.Region == "North");
            Assert.That(north.Total, Is.EqualTo(350m));
            Assert.That(north.Rows, Is.EqualTo(2));

            var south = regions.Single(_ => _.Region == "South");
            Assert.That(south.Total, Is.EqualTo(75m));
            Assert.That(south.Rows, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ResolvesCompositeKeyParts()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var groups = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, _.Grade}, (key, orders) => new {key.Region, key.Grade, Total = orders.Sum(_ => _.Amount)})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Count.EqualTo(3));
            Assert.That(groups.Single(_ => _.Region == "North" && _.Grade == 'A').Total, Is.EqualTo(100m));
            Assert.That(groups.Single(_ => _.Region == "North" && _.Grade == 'B').Total, Is.EqualTo(250m));
            Assert.That(groups.Single(_ => _.Region == "South" && _.Grade == 'A').Total, Is.EqualTo(75m));
        });
    }

    // The element-selector overload has no wire form, and silently grouping without it would answer
    // with aggregates over the wrong elements.
    [Test]
    public void ElementSelectorIsRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region, _ => _.Amount)
                .Select(_ => new {Total = _.Sum(v => v)})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("overload of GroupBy"));
    }

    // The result selector is the query's one Select, so writing another is a second projection.
    [Test]
    public void ASelectAfterTheResultSelectorIsASecond()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<ScryValidationException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region, (region, orders) => new {Region = region})
                .Select(_ => new {_.Region})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("Select"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
