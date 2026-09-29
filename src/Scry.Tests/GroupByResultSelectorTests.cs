/// <summary>
/// <c>GroupBy(key, (key, group) =&gt; …)</c> — sugar the client unfolds into the <c>GroupBy</c> +
/// <c>Select</c> it abbreviates, so the wire carries the same two operators either way.
/// </summary>
public class GroupByResultSelectorTests
{
    [Test]
    public async Task FoldsEachGroupThroughTheResultSelector()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var regions = await client.Source<Order>("Order")
            .GroupBy(
                _ => _.Region,
                (region, orders) => new
                {
                    Region = region,
                    // ReSharper disable PossibleMultipleEnumeration
                    Total = orders.Sum(_ => _.Amount),
                    Rows = orders.Count()
                    // ReSharper restore PossibleMultipleEnumeration
                })
            .ToListAsync();

        using (Assert.Multiple())
        {
            var north = regions.Single(_ => _.Region == "North");
            await Assert.That(north.Total).IsEqualTo(350m);
            await Assert.That(north.Rows).IsEqualTo(2);

            var south = regions.Single(_ => _.Region == "South");
            await Assert.That(south.Total).IsEqualTo(75m);
            await Assert.That(south.Rows).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ResolvesCompositeKeyParts()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var groups = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, _.Grade}, (key, orders) => new {key.Region, key.Grade, Total = orders.Sum(_ => _.Amount)})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(groups).Count().IsEqualTo(3);
            await Assert.That(groups.Single(_ => _.Region == "North" && _.Grade == 'A').Total).IsEqualTo(100m);
            await Assert.That(groups.Single(_ => _.Region == "North" && _.Grade == 'B').Total).IsEqualTo(250m);
            await Assert.That(groups.Single(_ => _.Region == "South" && _.Grade == 'A').Total).IsEqualTo(75m);
        }
    }

    // The element-selector overload has no wire form, and silently grouping without it would answer
    // with aggregates over the wrong elements.
    [Test]
    public async Task ElementSelectorIsRefused()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region, _ => _.Amount)
                .Select(_ => new {Total = _.Sum(v => v)})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("overload of GroupBy");
    }

    // The result selector is the query's one Select, so writing another is a second projection.
    [Test]
    public async Task ASelectAfterTheResultSelectorIsASecond()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() =>
            client.Source<Order>("Order")
                .GroupBy(_ => _.Region, (region, orders) => new {Region = region})
                .Select(_ => new {_.Region})
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("Select");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
