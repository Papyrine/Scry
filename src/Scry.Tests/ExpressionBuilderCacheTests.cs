/// <summary>
/// The members the builder reads by name on a type the wire decides — a temporal part, an optional's
/// Value, a grouping's Key, a deduplicated row's constructor and values — are found once per pairing
/// and shared, rather than looked up on every node of every request.
/// </summary>
public class ExpressionBuilderCacheTests
{
    [Test]
    public async Task AnOptionalsValueIsFoundOncePerClosing()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ExpressionBuilder.NullableValue(typeof(int?))).IsSameReferenceAs(ExpressionBuilder.NullableValue(typeof(int?)));
            await Assert.That(ExpressionBuilder.NullableValue(typeof(int?)).DeclaringType).IsEqualTo(typeof(int?));
            await Assert.That(ExpressionBuilder.NullableValue(typeof(int?))).IsNotSameReferenceAs(ExpressionBuilder.NullableValue(typeof(long?)));
        }
    }

    [Test]
    public async Task ATemporalPartIsFoundOncePerOwner()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ExpressionBuilder.Property(typeof(DateTime), "Year")).IsSameReferenceAs(ExpressionBuilder.Property(typeof(DateTime), "Year"));
            await Assert.That(ExpressionBuilder.Property(typeof(DateTime), "Year")).IsNotSameReferenceAs(ExpressionBuilder.Property(typeof(Date), "Year"));
            await Assert.That(ExpressionBuilder.Property(typeof(DateTime), "NoSuchPart")).IsNull();
        }
    }

    [Test]
    public async Task AGroupingsKeyIsFoundOncePerClosing()
    {
        var grouping = typeof(IGrouping<int, Employee>);

        using (Assert.Multiple())
        {
            await Assert.That(ExpressionBuilder.GroupingKey(grouping)).IsSameReferenceAs(ExpressionBuilder.GroupingKey(grouping));
            await Assert.That(ExpressionBuilder.GroupingKey(grouping).PropertyType).IsEqualTo(typeof(int));
        }
    }

    [Test]
    public async Task ADeduplicatedRowIsDescribedOncePerClosing()
    {
        var row = typeof(DistinctRow<int, string>);

        var (constructor, values) = DistinctRow.Describe(row);

        using (Assert.Multiple())
        {
            await Assert.That(constructor).IsEqualTo(DistinctRow.Describe(row).Constructor);
            await Assert.That(values).IsSameReferenceAs(DistinctRow.Describe(row).Values);
            await Assert.That(values.Select(_ => _.Name)).IsEquivalentTo(["Value1", "Value2"], CollectionOrdering.Matching);
            await Assert.That(values[1].PropertyType).IsEqualTo(typeof(string));
        }
    }
}
