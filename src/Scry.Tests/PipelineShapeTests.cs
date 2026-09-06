/// <summary>
/// The pipeline rules that existed in <c>ValidatePipeline</c> without a test naming them. Each is
/// pinned by its message, so a rule that stops firing fails here rather than reaching the executor
/// as a shape it was never written for.
/// </summary>
[TestFixture]
public class PipelineShapeTests
{
    static readonly SelectOp selectName = new(new([new("Name", new NodeValue(new MemberNode(["Name"])))]));

    static readonly OrderByOp byName = new(new MemberNode(["Name"]), Descending: false);

    [Test]
    public void OfTypeToASiblingAfterNarrowingIsRefused() =>
        Assert.That(
            Rejects("Asset", [new OfTypeOp("Vehicle"), new OfTypeOp("Building")]),
            Does.Contain("does not derive from 'Vehicle'"));

    // A complex type is never a source, so there is nothing for the narrowing to name.
    [Test]
    public void SelectManyOverAComplexCollectionThenOfTypeIsRefused() =>
        Assert.Throws<ScryValidationException>(
            () => Execute("Employee", [new SelectManyOp(["PreviousAddresses"]), new OfTypeOp("Employee"), new CountOp()]));

    [Test]
    public void ASecondGroupByIsRefused() =>
        Assert.That(
            Rejects("Order", [new GroupByOp([new MemberNode(["Region"])]), new GroupByOp([new MemberNode(["Region"])])]),
            Does.Contain("Only one GroupBy"));

    [Test]
    public void GroupByAfterSelectIsRefused() =>
        Assert.That(
            Rejects("Order", [new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])), new GroupByOp([new MemberNode(["Region"])])]),
            Does.Contain("GroupBy must precede Select"));

    [Test]
    public void GroupByWithoutASelectIsRefused() =>
        Assert.That(
            Rejects("Order", [new GroupByOp([new MemberNode(["Region"])])]),
            Does.Contain("GroupBy must be followed by a Select"));

    [TestCase("Where")]
    [TestCase("OrderBy")]
    [TestCase("OfType")]
    [TestCase("SelectMany")]
    [TestCase("Join")]
    public void AnOperatorAfterSelectIsRefused(string op)
    {
        QueryOp after = op switch
        {
            "Where" => new WhereOp(new MemberNode(["Active"])),
            "OrderBy" => byName,
            "OfType" => new OfTypeOp("Employee"),
            "SelectMany" => new SelectManyOp(["PreviousAddresses"]),
            _ => new JoinOp("Department", JoinKind.Inner, new MemberNode(["DepartmentId"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"])])
        };

        Assert.That(Rejects("Employee", [selectName, after]), Does.Contain("not allowed after Select"));
    }

    [TestCase("count")]
    [TestCase("first")]
    public void ATerminalPredicateAfterAJoinIsRefused(string terminal)
    {
        var join = new JoinOp("Department", JoinKind.Inner, new MemberNode(["DepartmentId"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"])]);
        QueryOp predicated = terminal == "count"
            ? new CountOp(new MemberNode(["Active"]))
            : new FirstOp(OrDefault: false, new MemberNode(["Active"]));

        Assert.That(Rejects("Employee", [join, predicated]), Does.Contain("terminal predicate is not allowed after a Join"));
    }

    [Test]
    public void PageAfterAJoinIsRefused()
    {
        var join = new JoinOp("Department", JoinKind.Inner, new MemberNode(["DepartmentId"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"])]);

        Assert.That(Rejects("Employee", [byName, join, new PageOp(Size: 1)]), Does.Contain("may follow a Join"));
    }

    [Test]
    public void PageAfterASetOperationIsRefused()
    {
        var set = new SetOp(SetKind.Union, "Employee", null, new([new("Name", new NodeValue(new MemberNode(["Name"])))]));

        Assert.That(Rejects("Employee", [byName, selectName, set, new PageOp(Size: 1)]), Does.Contain("may follow a set operation"));
    }

    static string Rejects(string root, IReadOnlyList<QueryOp> pipeline)
    {
        var exception = Assert.Throws<ScryValidationException>(() => Execute(root, pipeline))!;
        return exception.Message;
    }

    static QueryResponse Execute(string root, IReadOnlyList<QueryOp> pipeline)
    {
        using var context = TestContext.CreateSeeded();
        return SharedProcessor.Instance.Execute(QueryRequest.Create(root, pipeline), context);
    }
}
