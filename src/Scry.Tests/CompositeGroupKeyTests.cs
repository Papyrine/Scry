/// <summary>
/// Grouping by more than one member. A shaped row has no equality for a provider to group on, so the
/// key is projected into a <c>DistinctRow</c> that carries its member mappings — the same technique
/// that lets a multi-member Distinct be ordered and paged.
/// </summary>
public class CompositeGroupKeyTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record RegionCount(string Region, bool Discounted, int Count);

    record RegionTotal(string Region, char Grade, decimal Total);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task GroupByTwoMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientCompositeGroupBy
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, _.Grade})
            .Select(_ => new RegionTotal(_.Key.Region, _.Key.Grade, _.Sum(_ => _.Amount)))
            .ToListAsync();
        // end-snippet

        using (Assert.Multiple())
        {
            await Assert.That(rows).Count().IsEqualTo(3);
            await Assert.That(rows.Single(_ => _ is {Region: "North", Grade: 'A'}).Total).IsEqualTo(100m);
            await Assert.That(rows.Single(_ => _ is {Region: "North", Grade: 'B'}).Total).IsEqualTo(250m);
            await Assert.That(rows.Single(_ => _.Region == "South").Total).IsEqualTo(75m);
        }
    }

    [Test]
    public async Task ProjectsOnlySomeOfTheKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, _.Grade})
            .Select(_ => new {_.Key.Region, Count = _.Count()})
            .ToListAsync();

        await Assert.That(rows.Sum(_ => _.Count)).IsEqualTo(3);
    }

    [Test]
    public async Task GroupsByThreeMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, _.Grade, _.Quantity})
            .Select(_ => new {_.Key.Region, _.Key.Quantity, Total = _.Sum(_ => _.Amount)})
            .ToListAsync();

        await Assert.That(rows).Count().IsEqualTo(3);
    }

    [Test]
    public async Task FiltersGroupsByOnePartOfTheKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // HAVING over a composite key reads the same part the projection does.
        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, _.Grade})
            .Where(_ => _.Key.Region == "North")
            .Select(_ => new RegionTotal(_.Key.Region, _.Key.Grade, _.Sum(_ => _.Amount)))
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Grade).Order()).IsEquivalentTo(['A', 'B'], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OrdersTheGroupedRowsByAnAggregate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => new {_.Region, _.Grade})
            .Select(_ => new RegionCount(_.Key.Region, _.Key.Grade == 'A', _.Count()))
            .ToListAsync();

        await Assert.That(rows.Count(_ => _.Discounted)).IsEqualTo(2);
    }

    [Test]
    public async Task RejectsAKeyPartTheQueryDidNotGroupBy()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Reaching a member off the key that is not part of it is refused rather than silently
        // becoming a read of an ungrouped row member.
        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .GroupBy(_ => new {_.Region, _.Grade})
                .Select(_ => new {_.Key.Region, Other = _.Key.GetHashCode()})
                .ToListAsync());

        await Assert.That(exception).IsNotNull();
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
