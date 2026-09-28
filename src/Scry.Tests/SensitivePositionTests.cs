/// <summary>
/// A constant against a <c>[Sensitive]</c> member is a body, and a marked member in the result is
/// <c>no-store</c>, at every position the walk reaches — not only the root predicate the other
/// fixtures pin. Each shape here is one <c>SensitiveWalk</c> visits by inspection: a join's inner
/// side, a set operand, a subquery, a membership test, a grouping, and the two projections that
/// are not a <c>Select</c>.
/// </summary>
public class SensitivePositionTests
{
    static ConstNode text = new("x", ClrTypeTag.String);

    static BinaryNode Compared(params string[] path) =>
        new(BinaryOp.Equal, new MemberNode(path), text);

    static ProjectionMember Named(string name, params string[] path) =>
        new(name, new NodeValue(new MemberNode(path)));

    public static IEnumerable<TestDataRow<QueryRequest>> ConstantPositions()
    {
        yield return new(
            QueryRequest.Create(
                "Employee",
                [
                    new JoinOp("Invoice", JoinKind.Inner, new MemberNode(["Id"]), new MemberNode(["Id"]), Compared("Reviewer"), [new("Name", JoinSide.Outer, ["Name"])])
                ]), DisplayName: "a join's inner predicate");
        yield return new(
            QueryRequest.Create(
                "Employee",
                [
                    new SelectOp(new([Named("Name", "Name")])),
                    new SetOp(SetKind.Union, "Employee", Compared("Workstation", "Extension"), new([Named("Name", "Name")])),
                    new CountOp()
                ]), DisplayName: "a set operand's predicate");
        yield return new(
            QueryRequest.Create(
                "Employee",
                [new WhereOp(new SubqueryNode(["PreviousAddresses"], SubqueryFn.Any, Compared("City"))), new CountOp()]), DisplayName: "a subquery predicate");
        yield return new(
            QueryRequest.Create(
                "Employee",
                [new WhereOp(new InSourceNode(new MemberNode(["Id"]), "Employee", new MemberNode(["Id"]), Compared("Workstation", "Extension"))), new CountOp()]), DisplayName: "a membership test's predicate");
        yield return new(
            QueryRequest.Create(
                "Employee",
                [new WhereOp(new InSourceNode(text, "Invoice", new MemberNode(["Reviewer"]))), new CountOp()]), DisplayName: "a membership test's selector");
        yield return new(
            QueryRequest.Create(
                "Employee",
                [
                    new GroupByOp([new MemberNode(["Workstation", "Extension"])]),
                    new WhereOp(new BinaryNode(BinaryOp.Equal, new GroupKeyNode(0), text)),
                    new SelectOp(new([new("Extension", new NodeValue(new GroupKeyNode(0)))]))
                ]), DisplayName: "a HAVING clause over a marked group key");
        yield return new(
            QueryRequest.Create(
                "Employee",
                [
                    new GroupByOp([Compared("Workstation", "Extension")]),
                    new SelectOp(new([new("Matches", new NodeValue(new GroupKeyNode(0)))]))
                ]), DisplayName: "a computed group key");
    }

    [Test]
    [MethodDataSource(nameof(ConstantPositions))]
    public async Task AConstantAgainstAMarkedMemberIsRefusedFromAUrl(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Execute(request, context, fromUrl: true, out _))!;

        using (Assert.Multiple())
        {
            await Assert.That(exception.RequiresBody).IsTrue();
            await Assert.That(exception.Message).Contains("request body");
        }
    }

    [Test]
    [MethodDataSource(nameof(ConstantPositions))]
    public async Task TheSameQueryIsAnsweredFromABody(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();

        await Assert.That(() => Execute(request, context, fromUrl: false, out _)).ThrowsNothing();
    }

    public static IEnumerable<TestDataRow<QueryRequest>> ProjectedPositions()
    {
        yield return new(
            QueryRequest.Create(
                "Employee",
                [
                    new JoinOp("Invoice", JoinKind.Inner, new MemberNode(["Id"]), new MemberNode(["Id"]), null, [new("Name", JoinSide.Outer, ["Name"]), new("Reviewer", JoinSide.Inner, ["Reviewer"])])
                ]), DisplayName: "a join result");
        yield return new(
            QueryRequest.Create(
                "Employee",
                [
                    new SelectOp(new([Named("Name", "Name")])),
                    new SetOp(SetKind.Union, "Invoice", null, new([Named("Name", "Reviewer")]))
                ]), DisplayName: "a set operand's projection");
    }

    [Test]
    [MethodDataSource(nameof(ProjectedPositions))]
    public async Task AMarkedMemberInTheResultIsNotStorable(QueryRequest request)
    {
        using var context = TestContext.CreateSeeded();

        Execute(request, context, fromUrl: true, out var responseHeaders);

        await Assert.That(responseHeaders.CacheControl.ToString()).Contains("no-store");
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
