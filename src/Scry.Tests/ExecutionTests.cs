namespace Scry.Tests;

[TestFixture]
public class ExecutionTests
{
    [Test]
    public Task WhereOrderByNestedProjection()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Status"]),
                        new ConstNode("FullTime", ClrTypeTag.Enum))),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(
                    new(
                    [
                        new("Name", new ExprValue(new MemberNode(["Name"]))),
                        new("ManagerName", new ExprValue(new MemberNode(["Manager", "Name"]))),
                        new("Department", new NestedValue(
                            ["Department"],
                            new([new("Name", new ExprValue(new MemberNode(["Name"])))])))
                    ]))
            ]);

        return VerifyResponse(request);
    }

    [Test]
    public async Task DefaultProjectionExcludesIgnoredProperty()
    {
        var request = QueryRequest.Create("Employee", [new OrderByOp(new MemberNode(["Name"]), false)]);

        using var context = TestContext.CreateSeeded();
        var response = Processor().Execute(request, context);
        var json = ScryJson.Serialize(response);

        Assert.That(json, Does.Not.Contain("Salary").IgnoreCase);
        await VerifyResponse(request);
    }

    [Test]
    public Task GroupByWithAggregates()
    {
        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Region", new ExprValue(new MemberNode(["Region"]))),
                        new("Total", new ExprValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"])))),
                        new("Count", new ExprValue(new AggregateNode(AggregateFn.Count, Selector: null)))
                    ]))
            ]);

        return VerifyResponse(request);
    }

    [Test]
    public Task PocoSource()
    {
        var request = QueryRequest.Create(
            "Holiday",
            [
                new WhereOp(
                    new CallNode(
                        KnownFunction.StringContains,
                        new MemberNode(["Name"]),
                        [new ConstNode("a", ClrTypeTag.String)])),
                new SelectOp(new([new("Name", new ExprValue(new MemberNode(["Name"])))]))
            ]);

        return VerifyResponse(request);
    }

    [Test]
    public Task CountTerminal()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new MemberNode(["Active"])),
                new CountOp()
            ]);

        return VerifyResponse(request);
    }

    [Test]
    public Task FirstWithStringFunction()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), false),
                new FirstOp(OrDefault: true, new CallNode(
                    KnownFunction.StringStartsWith,
                    new MemberNode(["Name"]),
                    [new ConstNode("B", ClrTypeTag.String)]))
            ]);

        return VerifyResponse(request);
    }

    [Test]
    public Task PolicyScopesRowsBeforeClientFilter()
    {
        // No client filter on Active, but the policy restricts to active rows (Bob is inactive).
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), false),
                new SelectOp(new([new("Name", new ExprValue(new MemberNode(["Name"])))]))
            ]);

        using var context = TestContext.CreateSeeded();
        // begin-snippet: addPolicy
        var response = Processor(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>()).Execute(request, context);
        // end-snippet
        return Verify(Pretty(ScryJson.Serialize(response)));
    }

    static Task VerifyResponse(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();
        var response = Processor().Execute(request, context);
        return Verify(Pretty(ScryJson.Serialize(response)));
    }

    // begin-snippet: processorCreate
    static ScryProcessor Processor(Action<ScryOptions>? extra = null) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra?.Invoke(options);
        });
    // end-snippet

    static readonly JsonSerializerOptions indented = new() { WriteIndented = true };

    static string Pretty(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json), indented);
}
