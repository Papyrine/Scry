namespace Scry.Tests;

[TestFixture]
public class SecurityTests
{
    // begin-snippet: rejectIgnoredProperty
    [Test]
    public void RejectsIgnoredProperty() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.GreaterThan,
                    new MemberNode(["Salary"]),
                    new ConstNode("100", ClrTypeTag.Decimal)))
            ]));
    // end-snippet

    [Test]
    public void RejectsUnknownRoot() =>
        AssertRejected(QueryRequest.Create("Secret", []));

    [Test]
    public void RejectsUnknownProperty() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Ssn"]), new ConstNode("x", ClrTypeTag.String)))]));

    [Test]
    public void RejectsTraversalThroughScalar() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Name", "Length"]), new ConstNode("3", ClrTypeTag.Int32)))]));

    // A [QueryIgnore] member of a complex type is hidden just like on an entity — traversing to it is
    // rejected, so a JSON column cannot smuggle in an unlisted field.
    [Test]
    public void RejectsIgnoredComplexMember() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Address", "Zip"]), new ConstNode("x", ClrTypeTag.String)))]));

    // A complex member is not a scalar; using it where a value is required is rejected (you must name
    // a scalar leaf such as Address.City).
    [Test]
    public void RejectsComplexMemberAsScalar() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Address"]), new ConstNode("x", ClrTypeTag.String)))]));

    [Test]
    public void RejectsTakeOverMaxPageSize() =>
        AssertRejected(
            QueryRequest.Create("Employee", [new TakeOp(50)]),
            options => options.MaxPageSize = 2);

    [Test]
    public void RejectsPageSizeOverMaxPageSize() =>
        AssertRejected(
            QueryRequest.Create("Employee", [new PageOp(50)]),
            options => options.MaxPageSize = 2);

    [Test]
    public void RejectsInvalidPagingCursor() =>
        // Ordered query is seek-safe, so the server tries to decode the (garbage) cursor and rejects it.
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new OrderByOp(new MemberNode(["Name"]), false), new PageOp(2, "not-a-valid-cursor")]));

    [Test]
    public void RejectsCursorOnUnorderedQuery() =>
        // A cursor needs an ordering to resume; an unordered page with a cursor is rejected.
        AssertRejected(QueryRequest.Create("Employee", [new PageOp(2, "anything")]));

    [Test]
    public void RejectsPagingGroupedQuery() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])),
                new PageOp(10)
            ]));

    [Test]
    public void RejectsAggregateWithoutGroupBy() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"]))))]))]));

    [Test]
    public void RejectsThenByWithoutOrderBy() =>
        AssertRejected(QueryRequest.Create("Employee", [new ThenByOp(new MemberNode(["Name"]), false)]));

    [Test]
    public void RejectsOperatorAfterTerminal() =>
        AssertRejected(QueryRequest.Create("Employee", [new CountOp(), new TakeOp(5)]));

    [Test]
    public void RejectsUnsupportedWireVersion() =>
        AssertRejected(new(99, "Employee", []));

    [Test]
    public void RejectsGroupedProjectionReferencingNonKey() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Amount", new NodeValue(new MemberNode(["Amount"])))]))
            ]));

    static void AssertRejected(QueryRequest request, Action<ScryOptions>? extra = null)
    {
        using var context = TestContext.CreateSeeded();
        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra?.Invoke(options);
        });

        Assert.Throws<ScryValidationException>(() => processor.Execute(request, context));
    }
}
