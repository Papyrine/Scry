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
                    new BinaryNode(
                        BinaryOp.AndAlso,
                        new BinaryNode(
                            BinaryOp.Equal,
                            new MemberNode(["Status"]),
                            new ConstNode("FullTime", ClrTypeTag.Enum)),
                        new CallNode(
                            KnownFunction.StringStartsWith,
                            new MemberNode(["Name"]),
                            [new ConstNode("A", ClrTypeTag.String)]))),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new ThenByOp(new MemberNode(["Id"]), Descending: true),
                new SkipOp(10),
                new TakeOp(50),
                new SelectOp(
                    new(
                    [
                        new("Name", new NodeValue(new MemberNode(["Name"]))),
                        new("ManagerName", new NodeValue(new MemberNode(["Manager", "Name"]))),
                        new(
                            "Manager",
                            new NestedValue(
                                ["Manager"],
                                new([new("Name", new NodeValue(new MemberNode(["Name"])))])))
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
                    new BinaryNode(
                        BinaryOp.GreaterThan,
                        new MemberNode(["Amount"]),
                        new ConstNode("0", ClrTypeTag.Decimal))),
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Region", new NodeValue(new MemberNode(["Region"]))),
                        new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"])))),
                        new("Count", new NodeValue(new AggregateNode(AggregateFn.Count, Selector: null)))
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
                    new UnaryNode(
                        UnaryOp.Not,
                        new CallNode(KnownFunction.StringIsNullOrEmpty, new MemberNode(["Name"]), []))),
                new FirstOp(OrDefault: true, new MemberNode(["Active"]))
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
