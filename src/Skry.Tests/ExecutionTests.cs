using System.Text.Json;
using Skry;

namespace Skry.Tests;

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
                    new BinaryExpr(
                        BinaryOp.Equal,
                        new MemberExpr(["Status"]),
                        new ConstExpr("FullTime", ClrTypeTag.Enum))),
                new OrderByOp(new MemberExpr(["Name"]), Descending: false),
                new SelectOp(
                    new Projection(
                    [
                        new("Name", new ExprValue(new MemberExpr(["Name"]))),
                        new("ManagerName", new ExprValue(new MemberExpr(["Manager", "Name"]))),
                        new("Department", new NestedValue(
                            ["Department"],
                            new Projection([new("Name", new ExprValue(new MemberExpr(["Name"])))])))
                    ]))
            ]);

        return VerifyResponse(request);
    }

    [Test]
    public async Task DefaultProjectionExcludesIgnoredProperty()
    {
        var request = QueryRequest.Create("Employee", [new OrderByOp(new MemberExpr(["Name"]), false)]);

        using var context = TestContext.CreateSeeded();
        var response = Processor().Execute(request, context);
        var json = SkryJson.Serialize(response);

        Assert.That(json, Does.Not.Contain("Salary").IgnoreCase);
        await VerifyResponse(request);
    }

    [Test]
    public Task GroupByWithAggregates()
    {
        var request = QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberExpr(["Region"])]),
                new SelectOp(
                    new Projection(
                    [
                        new("Region", new ExprValue(new MemberExpr(["Region"]))),
                        new("Total", new ExprValue(new AggregateExpr(AggregateFn.Sum, new MemberExpr(["Amount"])))),
                        new("Count", new ExprValue(new AggregateExpr(AggregateFn.Count, Selector: null)))
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
                    new CallExpr(
                        KnownFunction.StringContains,
                        new MemberExpr(["Name"]),
                        [new ConstExpr("a", ClrTypeTag.String)])),
                new SelectOp(new Projection([new("Name", new ExprValue(new MemberExpr(["Name"])))]))
            ]);

        return VerifyResponse(request);
    }

    [Test]
    public Task CountTerminal()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new MemberExpr(["Active"])),
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
                new OrderByOp(new MemberExpr(["Name"]), false),
                new FirstOp(OrDefault: true, new CallExpr(
                    KnownFunction.StringStartsWith,
                    new MemberExpr(["Name"]),
                    [new ConstExpr("B", ClrTypeTag.String)]))
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
                new OrderByOp(new MemberExpr(["Name"]), false),
                new SelectOp(new Projection([new("Name", new ExprValue(new MemberExpr(["Name"])))]))
            ]);

        using var context = TestContext.CreateSeeded();
        var response = Processor(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>()).Execute(request, context);
        return Verify(Pretty(SkryJson.Serialize(response)));
    }

    static Task VerifyResponse(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();
        var response = Processor().Execute(request, context);
        return Verify(Pretty(SkryJson.Serialize(response)));
    }

    static SkryProcessor Processor(Action<SkryOptions>? extra = null) =>
        SkryProcessor.Create(options =>
        {
            options.UseModel<TestContext>();
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra?.Invoke(options);
        });

    static readonly JsonSerializerOptions indented = new() { WriteIndented = true };

    static string Pretty(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json), indented);
}
