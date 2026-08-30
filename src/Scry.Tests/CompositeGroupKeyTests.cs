/// <summary>
/// Grouping by more than one member. A shaped row has no equality for a provider to group on, so the
/// key is projected into a <c>DistinctRow</c> that carries its member mappings — the same technique
/// that lets a multi-member Distinct be ordered and paged.
/// </summary>
[TestFixture]
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

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(rows.Single(_ => _ is {Region: "North", Grade: 'A'}).Total, Is.EqualTo(100m));
            Assert.That(rows.Single(_ => _ is {Region: "North", Grade: 'B'}).Total, Is.EqualTo(250m));
            Assert.That(rows.Single(_ => _.Region == "South").Total, Is.EqualTo(75m));
        });
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

        Assert.That(rows.Sum(_ => _.Count), Is.EqualTo(3));
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

        Assert.That(rows, Has.Count.EqualTo(3));
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

        Assert.That(rows.Select(_ => _.Grade).Order(), Is.EqualTo(['A', 'B']));
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

        Assert.That(rows.Count(_ => _.Discounted), Is.EqualTo(2));
    }

    [Test]
    public void RejectsAKeyPartTheQueryDidNotGroupBy()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Reaching a member off the key that is not part of it is refused rather than silently
        // becoming a read of an ungrouped row member.
        var exception = Assert.ThrowsAsync<NotSupportedException>(
            () => client.Source<Order>("Order")
                .GroupBy(_ => new {_.Region, _.Grade})
                .Select(_ => new {_.Key.Region, Other = _.Key.GetHashCode()})
                .ToListAsync());

        Assert.That(exception, Is.Not.Null);
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
