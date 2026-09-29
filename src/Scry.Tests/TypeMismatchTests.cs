/// <summary>
/// Requests that resolve every member and pass every shape check, and pair types no operator is
/// defined for: a text member as a predicate, a conjunction of text and a bool, a negated name. Each
/// was once a server fault — the expression tree refused it while the query was being built, past
/// the one operator that caught its own — and is a rejection now. Hand-built, since no client can
/// write them.
/// </summary>
public class TypeMismatchTests
{
    static MemberNode name = new(["Name"]);
    static MemberNode active = new(["Active"]);
    static MemberNode managerId = new(["ManagerId"]);

    [Test]
    [MethodDataSource(nameof(Mismatches))]
    public async Task IsRejected(string label, Node predicate)
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Employee", [new WhereOp(predicate), new CountOp()]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("cannot be built").Or.Contains("must be a condition").Because(label);
    }

    public static IEnumerable<TestDataRow<(string, Node)>> Mismatches()
    {
        yield return new(("a text member as the predicate", name), DisplayName: "IsRejected(text as a predicate)");
        yield return new(("text and a bool", new BinaryNode(BinaryOp.AndAlso, name, active)), DisplayName: "IsRejected(AndAlso over text)");
        yield return new(("text or a bool", new BinaryNode(BinaryOp.OrElse, name, active)), DisplayName: "IsRejected(OrElse over text)");
        yield return new(("not over text", new UnaryNode(UnaryOp.Not, name)), DisplayName: "IsRejected(Not over text)");
        yield return new(("negated text", new BinaryNode(BinaryOp.Equal, new UnaryNode(UnaryOp.Negate, name), name)), DisplayName: "IsRejected(Negate over text)");
        yield return new(("a name as a condition's test", new ConditionalNode(name, active, active)), DisplayName: "IsRejected(text as a test)");
        yield return new(("a number coalesced with text", new BinaryNode(BinaryOp.Equal, new BinaryNode(BinaryOp.Coalesce, managerId, name), name)), DisplayName: "IsRejected(Coalesce of a number with text)");
    }

    // The same shape inside a HAVING, where the row is a group.
    [Test]
    public async Task AGroupedPredicateThatIsNotAConditionIsRejected()
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new GroupByOp([new MemberNode(["DepartmentId"])]),
                new WhereOp(new MemberNode(["DepartmentId"])),
                new SelectOp(new([new("Department", new NodeValue(new MemberNode(["DepartmentId"])))]))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("must be a condition");
    }
}
