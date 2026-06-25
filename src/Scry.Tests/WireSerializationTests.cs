namespace Scry.Tests;

[TestFixture]
public class WireSerializationTests
{
    [Test]
    public Task FullPipelineRoundTrips()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new BinaryExpr(
                        BinaryOp.AndAlso,
                        new BinaryExpr(
                            BinaryOp.Equal,
                            new MemberExpr(["Status"]),
                            new ConstExpr("FullTime", ClrTypeTag.Enum)),
                        new CallExpr(
                            KnownFunction.StringStartsWith,
                            new MemberExpr(["Name"]),
                            [new ConstExpr("A", ClrTypeTag.String)]))),
                new OrderByOp(new MemberExpr(["Name"]), Descending: false),
                new ThenByOp(new MemberExpr(["Id"]), Descending: true),
                new SkipOp(10),
                new TakeOp(50),
                new SelectOp(
                    new(
                    [
                        new("Name", new ExprValue(new MemberExpr(["Name"]))),
                        new("ManagerName", new ExprValue(new MemberExpr(["Manager", "Name"]))),
                        new(
                            "Manager",
                            new NestedValue(
                                ["Manager"],
                                new([new("Name", new ExprValue(new MemberExpr(["Name"])))])))
                    ]))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task GroupByAggregateRoundTrips()
    {
        var request = QueryRequest.Create(
            "Orders",
            [
                new WhereOp(
                    new BinaryExpr(
                        BinaryOp.GreaterThan,
                        new MemberExpr(["Amount"]),
                        new ConstExpr("0", ClrTypeTag.Decimal))),
                new GroupByOp([new MemberExpr(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Region", new ExprValue(new MemberExpr(["Region"]))),
                        new("Total", new ExprValue(new AggregateExpr(AggregateFn.Sum, new MemberExpr(["Amount"])))),
                        new("Count", new ExprValue(new AggregateExpr(AggregateFn.Count, Selector: null)))
                    ]))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task TerminalsRoundTrip()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new UnaryExpr(
                        UnaryOp.Not,
                        new CallExpr(KnownFunction.StringIsNullOrEmpty, new MemberExpr(["Name"]), []))),
                new FirstOp(OrDefault: true, new MemberExpr(["Active"]))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public void UnknownDiscriminatorFailsClosed()
    {
        var json = """{"version":1,"root":"Employees","pipeline":[{"$type":"evil","predicate":null}]}""";

        Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
    }

    [Test]
    public void MalformedJsonFailsClosed() =>
        Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest("{ not json"));

    static Task VerifyRoundTrip(QueryRequest request)
    {
        var json = ScryJson.Serialize(request);
        var roundTripped = ScryJson.DeserializeRequest(json);
        var reserialized = ScryJson.Serialize(roundTripped);

        Assert.That(reserialized, Is.EqualTo(json));

        return Verify(json);
    }
}
