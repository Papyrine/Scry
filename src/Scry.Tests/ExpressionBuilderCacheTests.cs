/// <summary>
/// The members the builder reads by name on a type the wire decides — a temporal part, an optional's
/// Value, a grouping's Key, a deduplicated row's constructor and values — are found once per pairing
/// and shared, rather than looked up on every node of every request.
/// </summary>
[TestFixture]
public class ExpressionBuilderCacheTests
{
    [Test]
    public void AnOptionalsValueIsFoundOncePerClosing() =>
        Assert.Multiple(() =>
        {
            Assert.That(ExpressionBuilder.NullableValue(typeof(int?)), Is.SameAs(ExpressionBuilder.NullableValue(typeof(int?))));
            Assert.That(ExpressionBuilder.NullableValue(typeof(int?)).DeclaringType, Is.EqualTo(typeof(int?)));
            Assert.That(ExpressionBuilder.NullableValue(typeof(int?)), Is.Not.SameAs(ExpressionBuilder.NullableValue(typeof(long?))));
        });

    [Test]
    public void ATemporalPartIsFoundOncePerOwner() =>
        Assert.Multiple(() =>
        {
            Assert.That(ExpressionBuilder.Property(typeof(DateTime), "Year"), Is.SameAs(ExpressionBuilder.Property(typeof(DateTime), "Year")));
            Assert.That(ExpressionBuilder.Property(typeof(DateTime), "Year"), Is.Not.SameAs(ExpressionBuilder.Property(typeof(Date), "Year")));
            Assert.That(ExpressionBuilder.Property(typeof(DateTime), "NoSuchPart"), Is.Null);
        });

    [Test]
    public void AGroupingsKeyIsFoundOncePerClosing()
    {
        var grouping = typeof(IGrouping<int, Employee>);

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionBuilder.GroupingKey(grouping), Is.SameAs(ExpressionBuilder.GroupingKey(grouping)));
            Assert.That(ExpressionBuilder.GroupingKey(grouping).PropertyType, Is.EqualTo(typeof(int)));
        });
    }

    [Test]
    public void ADeduplicatedRowIsDescribedOncePerClosing()
    {
        var row = typeof(DistinctRow<int, string>);

        var (constructor, values) = DistinctRow.Describe(row);

        Assert.Multiple(() =>
        {
            Assert.That(constructor, Is.EqualTo(DistinctRow.Describe(row).Constructor));
            Assert.That(values, Is.SameAs(DistinctRow.Describe(row).Values));
            Assert.That(values.Select(_ => _.Name), Is.EqualTo(["Value1", "Value2"]));
            Assert.That(values[1].PropertyType, Is.EqualTo(typeof(string)));
        });
    }
}
