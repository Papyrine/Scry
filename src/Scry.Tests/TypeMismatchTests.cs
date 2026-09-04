/// <summary>
/// Requests that resolve every member and pass every shape check, and pair types no operator is
/// defined for: a text member as a predicate, a conjunction of text and a bool, a negated name. Each
/// was once a server fault — the expression tree refused it while the query was being built, past
/// the one operator that caught its own — and is a rejection now. Hand-built, since no client can
/// write them.
/// </summary>
[TestFixture]
public class TypeMismatchTests
{
    static readonly MemberNode name = new(["Name"]);
    static readonly MemberNode active = new(["Active"]);
    static readonly MemberNode managerId = new(["ManagerId"]);

    [TestCaseSource(nameof(Mismatches))]
    public void IsRejected(string label, Node predicate)
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Employee", [new WhereOp(predicate), new CountOp()]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("cannot be built").Or.Contain("must be a condition"), label);
    }

    static IEnumerable<TestCaseData> Mismatches()
    {
        yield return new TestCaseData("a text member as the predicate", name).SetName("{m}(text as a predicate)");
        yield return new TestCaseData("text and a bool", new BinaryNode(BinaryOp.AndAlso, name, active)).SetName("{m}(AndAlso over text)");
        yield return new TestCaseData("text or a bool", new BinaryNode(BinaryOp.OrElse, name, active)).SetName("{m}(OrElse over text)");
        yield return new TestCaseData("not over text", new UnaryNode(UnaryOp.Not, name)).SetName("{m}(Not over text)");
        yield return new TestCaseData("negated text", new BinaryNode(BinaryOp.Equal, new UnaryNode(UnaryOp.Negate, name), name)).SetName("{m}(Negate over text)");
        yield return new TestCaseData("a name as a condition's test", new ConditionalNode(name, active, active)).SetName("{m}(text as a test)");
        yield return new TestCaseData("a number coalesced with text", new BinaryNode(BinaryOp.Equal, new BinaryNode(BinaryOp.Coalesce, managerId, name), name)).SetName("{m}(Coalesce of a number with text)");
    }

    // The same shape inside a HAVING, where the row is a group.
    [Test]
    public void AGroupedPredicateThatIsNotAConditionIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new GroupByOp([new MemberNode(["DepartmentId"])]),
                new WhereOp(new MemberNode(["DepartmentId"])),
                new SelectOp(new([new("Department", new NodeValue(new MemberNode(["DepartmentId"])))]))
            ]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("must be a condition"));
    }
}
