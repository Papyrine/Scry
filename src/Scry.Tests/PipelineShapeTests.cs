/// <summary>
/// The pipeline rules that existed in <c>ValidatePipeline</c> without a test naming them. Each is
/// pinned by its message, so a rule that stops firing fails here rather than reaching the executor
/// as a shape it was never written for.
/// </summary>
public class PipelineShapeTests
{
    static SelectOp selectName = new(new([new("Name", new NodeValue(new MemberNode(["Name"])))]));

    static OrderByOp byName = new(new MemberNode(["Name"]), Descending: false);

    [Test]
    public async Task OfTypeToASiblingAfterNarrowingIsRefused() =>
        await Assert.That(Rejects("Asset", [new OfTypeOp("Vehicle"), new OfTypeOp("Building")])).Contains("does not derive from 'Vehicle'");

    // A complex type is never a source, so there is nothing for the narrowing to name.
    [Test]
    public void SelectManyOverAComplexCollectionThenOfTypeIsRefused() =>
        Assert.ThrowsExactly<ScryValidationException>(
            () => Execute("Employee", [new SelectManyOp(["PreviousAddresses"]), new OfTypeOp("Employee"), new CountOp()]));

    [Test]
    public async Task ASecondGroupByIsRefused() =>
        await Assert.That(Rejects("Order", [new GroupByOp([new MemberNode(["Region"])]), new GroupByOp([new MemberNode(["Region"])])])).Contains("Only one GroupBy");

    [Test]
    public async Task GroupByAfterSelectIsRefused() =>
        await Assert.That(Rejects("Order", [new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])), new GroupByOp([new MemberNode(["Region"])])])).Contains("GroupBy must precede Select");

    [Test]
    public async Task GroupByWithoutASelectIsRefused() =>
        await Assert.That(Rejects("Order", [new GroupByOp([new MemberNode(["Region"])])])).Contains("GroupBy must be followed by a Select");

    // The executor slices rows as it meets the slice and applies the grouping after every row
    // operator, so a slice written after GroupBy was answered with aggregates over the sliced rows
    // rather than with a slice of the groups. An ordering written before the grouping is what let it
    // through: it ordered the rows, and the groups have none.
    [Test]
    [Arguments("Skip")]
    [Arguments("Take")]
    [Arguments("Last")]
    public async Task ASliceAfterGroupByIsRefused(string op)
    {
        QueryOp after = op switch
        {
            "Skip" => new SkipOp(1),
            "Take" => new TakeOp(2),
            _ => new LastOp(OrDefault: false, Predicate: null)
        };

        await Assert.That(Rejects("Order", [byRegion, groupByRegion, regionCounts, after])).Contains($"{op} is not allowed after GroupBy");
    }

    [Test]
    public async Task ASliceBeforeGroupByGroupsTheSlicedRows()
    {
        var response = Execute("Order", [new OrderByOp(new MemberNode(["Id"]), Descending: false), new TakeOp(1), groupByRegion, regionCounts]);

        var group = response.Payload.EnumerateArray().Single();
        await Assert.That(group.GetProperty("count").GetInt32()).IsEqualTo(1);
    }

    static OrderByOp byRegion = new(new MemberNode(["Region"]), Descending: false);

    static GroupByOp groupByRegion = new([new MemberNode(["Region"])]);

    static SelectOp regionCounts = new(
        new(
        [
            new("Region", new NodeValue(new MemberNode(["Region"]))),
            new("Count", new NodeValue(new AggregateNode(AggregateFn.Count, Selector: null)))
        ]));

    [Test]
    [Arguments("Where")]
    [Arguments("OrderBy")]
    [Arguments("OfType")]
    [Arguments("SelectMany")]
    [Arguments("Join")]
    public async Task AnOperatorAfterSelectIsRefused(string op)
    {
        QueryOp after = op switch
        {
            "Where" => new WhereOp(new MemberNode(["Active"])),
            "OrderBy" => byName,
            "OfType" => new OfTypeOp("Employee"),
            "SelectMany" => new SelectManyOp(["PreviousAddresses"]),
            _ => new JoinOp("Department", JoinKind.Inner, new MemberNode(["DepartmentId"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"])])
        };

        await Assert.That(Rejects("Employee", [selectName, after])).Contains("not allowed after Select");
    }

    [Test]
    [Arguments("count")]
    [Arguments("first")]
    public async Task ATerminalPredicateAfterAJoinIsRefused(string terminal)
    {
        var join = new JoinOp("Department", JoinKind.Inner, new MemberNode(["DepartmentId"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"])]);
        QueryOp predicated = terminal == "count"
            ? new CountOp(new MemberNode(["Active"]))
            : new FirstOp(OrDefault: false, new MemberNode(["Active"]));

        await Assert.That(Rejects("Employee", [join, predicated])).Contains("terminal predicate is not allowed after a Join");
    }

    [Test]
    public async Task PageAfterAJoinIsRefused()
    {
        var join = new JoinOp("Department", JoinKind.Inner, new MemberNode(["DepartmentId"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"])]);

        await Assert.That(Rejects("Employee", [byName, join, new PageOp(Size: 1)])).Contains("may follow a Join");
    }

    [Test]
    public async Task PageAfterASetOperationIsRefused()
    {
        var set = new SetOp(SetKind.Union, "Employee", null, new([new("Name", new NodeValue(new MemberNode(["Name"])))]));

        await Assert.That(Rejects("Employee", [byName, selectName, set, new PageOp(Size: 1)])).Contains("may follow a set operation");
    }

    static string Rejects(string root, IReadOnlyList<QueryOp> pipeline)
    {
        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Execute(root, pipeline));
        return exception.Message;
    }

    static QueryResponse Execute(string root, IReadOnlyList<QueryOp> pipeline)
    {
        using var context = TestContext.CreateSeeded();
        return SharedProcessor.Instance.Execute(QueryRequest.Create(root, pipeline), context);
    }
}
