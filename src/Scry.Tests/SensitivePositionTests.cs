/// <summary>
/// A constant against a <c>[Sensitive]</c> member is a body, and a marked member in the result is
/// <c>no-store</c>, at every position the walk reaches — not only the root predicate the other
/// fixtures pin. Each shape here is one <c>SensitiveWalk</c> visits by inspection: a join's inner
/// side, a set operand, a subquery, a membership test, a grouping, and the two projections that
/// are not a <c>Select</c>.
/// </summary>
[TestFixture]
public class SensitivePositionTests
{
    static readonly ConstNode text = new("x", ClrTypeTag.String);

    static BinaryNode Compared(params string[] path) =>
        new(BinaryOp.Equal, new MemberNode(path), text);

    static ProjectionMember Named(string name, params string[] path) =>
        new(name, new NodeValue(new MemberNode(path)));

    static IEnumerable<TestCaseData> ConstantPositions()
    {
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [
                    new JoinOp("Invoice", JoinKind.Inner, new MemberNode(["Id"]), new MemberNode(["Id"]), Compared("Reviewer"), [new("Name", JoinSide.Outer, ["Name"])])
                ])).SetName("a join's inner predicate");
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [
                    new SelectOp(new([Named("Name", "Name")])),
                    new SetOp(SetKind.Union, "Employee", Compared("Workstation", "Extension"), new([Named("Name", "Name")])),
                    new CountOp()
                ])).SetName("a set operand's predicate");
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [new WhereOp(new SubqueryNode(["PreviousAddresses"], SubqueryFn.Any, Compared("City"))), new CountOp()])).SetName("a subquery predicate");
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [new WhereOp(new InSourceNode(new MemberNode(["Id"]), "Employee", new MemberNode(["Id"]), Compared("Workstation", "Extension"))), new CountOp()])).SetName("a membership test's predicate");
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [new WhereOp(new InSourceNode(text, "Invoice", new MemberNode(["Reviewer"]))), new CountOp()])).SetName("a membership test's selector");
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [
                    new GroupByOp([new MemberNode(["Workstation", "Extension"])]),
                    new WhereOp(new BinaryNode(BinaryOp.Equal, new GroupKeyNode(0), text)),
                    new SelectOp(new([new("Extension", new NodeValue(new GroupKeyNode(0)))]))
                ])).SetName("a HAVING clause over a marked group key");
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [
                    new GroupByOp([Compared("Workstation", "Extension")]),
                    new SelectOp(new([new("Matches", new NodeValue(new GroupKeyNode(0)))]))
                ])).SetName("a computed group key");
    }

    [TestCaseSource(nameof(ConstantPositions))]
    public void AConstantAgainstAMarkedMemberIsRefusedFromAUrl(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<ScryValidationException>(() => Execute(request, context, fromUrl: true, out _))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.RequiresBody, Is.True);
            Assert.That(exception.Message, Does.Contain("request body"));
        });
    }

    [TestCaseSource(nameof(ConstantPositions))]
    public void TheSameQueryIsAnsweredFromABody(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();

        Assert.DoesNotThrow(() => Execute(request, context, fromUrl: false, out _));
    }

    static IEnumerable<TestCaseData> ProjectedPositions()
    {
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [
                    new JoinOp("Invoice", JoinKind.Inner, new MemberNode(["Id"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"]), new("Reviewer", JoinSide.Inner, ["Reviewer"])])
                ])).SetName("a join result");
        yield return new TestCaseData(
            QueryRequest.Create(
                "Employee",
                [
                    new SelectOp(new([Named("Name", "Name")])),
                    new SetOp(SetKind.Union, "Invoice", null, new([Named("Name", "Reviewer")]))
                ])).SetName("a set operand's projection");
    }

    [TestCaseSource(nameof(ProjectedPositions))]
    public void AMarkedMemberInTheResultIsNotStorable(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();

        Execute(request, context, fromUrl: true, out var responseHeaders);

        Assert.That(responseHeaders.CacheControl.ToString(), Does.Contain("no-store"));
    }

    static QueryResponse Execute(QueryRequest request, TestContext context, bool fromUrl, out IHeaderDictionary responseHeaders)
    {
        responseHeaders = new HeaderDictionary();
        return SharedProcessor.Instance.Execute(
            request,
            context,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            responseHeaders,
            binary: null,
            fromUrl);
    }
}
