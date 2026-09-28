/// <summary>
/// Distinct inside a grouped fold. The wire's flag means distinct selected values; over the group's
/// rows LINQ means distinct rows, which is every row. The row spelling was accepted and translated as
/// the value spelling, so two orders of equal amount summed once.
/// </summary>
public class GroupedDistinctTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record RegionTotal(string Region, decimal Total);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task DistinctOverTheSelectedValuesFolds()
    {
        var request = Client()
            .Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ =>
                new RegionTotal(
                    _.Key,
                    _.Select(_ => _.Amount).Distinct().Sum()))
            .ToScryRequest();

        var aggregate = (AggregateNode) ((NodeValue) ((SelectOp) request.Pipeline[1]).Projection.Members[1].Value).Node;

        await Assert.That(aggregate.Distinct).IsTrue();
    }

    [Test]
    public async Task DistinctOverTheRowsIsRefused()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => Client()
                .Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new RegionTotal(_.Key, _.Distinct().Sum(_ => _.Amount)))
                .ToScryRequest());

        await Assert.That(exception!.Message).Contains("Select the value first");
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));
}
