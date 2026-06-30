namespace Scry.Tests;

[TestFixture]
public class SecurityTests
{
    [Test]
    public void RejectsIgnoredProperty() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryExpr(
                    BinaryOp.GreaterThan,
                    new MemberExpr(["Salary"]),
                    new ConstExpr("100", ClrTypeTag.Decimal)))
            ]));

    [Test]
    public void RejectsUnknownRoot() =>
        AssertRejected(QueryRequest.Create("Secret", []));

    [Test]
    public void RejectsUnknownProperty() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryExpr(BinaryOp.Equal, new MemberExpr(["Ssn"]), new ConstExpr("x", ClrTypeTag.String)))]));

    [Test]
    public void RejectsTraversalThroughScalar() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryExpr(BinaryOp.Equal, new MemberExpr(["Name", "Length"]), new ConstExpr("3", ClrTypeTag.Int32)))]));

    [Test]
    public void RejectsTakeOverMaxPageSize() =>
        AssertRejected(
            QueryRequest.Create("Employee", [new TakeOp(50)]),
            options => options.MaxPageSize = 2);

    [Test]
    public void RejectsAggregateWithoutGroupBy() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Total", new ExprValue(new AggregateExpr(AggregateFn.Sum, new MemberExpr(["Amount"]))))]))]));

    [Test]
    public void RejectsThenByWithoutOrderBy() =>
        AssertRejected(QueryRequest.Create("Employee", [new ThenByOp(new MemberExpr(["Name"]), false)]));

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
                new GroupByOp([new MemberExpr(["Region"])]),
                new SelectOp(new([new("Amount", new ExprValue(new MemberExpr(["Amount"])))]))
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
