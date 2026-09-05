/// <summary>
/// Distinct inside a grouped fold. The wire's flag means distinct selected values; over the group's
/// rows LINQ means distinct rows, which is every row. The row spelling was accepted and translated as
/// the value spelling, so two orders of equal amount summed once.
/// </summary>
[TestFixture]
public class GroupedDistinctTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record RegionTotal(string Region, decimal Total);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public void DistinctOverTheSelectedValuesFolds()
    {
        var request = Client().Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new RegionTotal(_.Key, _.Select(x => x.Amount).Distinct().Sum()))
            .ToScryRequest();

        var aggregate = (AggregateNode) ((NodeValue) ((SelectOp) request.Pipeline[1]).Projection.Members[1].Value).Node;

        Assert.That(aggregate.Distinct, Is.True);
    }

    [Test]
    public void DistinctOverTheRowsIsRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => Client().Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new RegionTotal(_.Key, _.Distinct().Sum(x => x.Amount)))
                .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("Select the value first"));
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));
}
