/// <summary>
/// A constant against a <c>[Sensitive]</c> member is a body, and a marked member in the result is
/// <c>no-store</c>, at every position the walk reaches — not only the root predicate the other
/// fixtures pin. Each shape here is one <c>SensitiveWalk</c> visits by inspection: a join's inner
/// side, a set operand, a subquery, a membership test, a grouping, the two projections that are not
/// a <c>Select</c>, the folds that return a value rather than a count, and the row a default
/// projection reads after a narrow or a flatten.
/// </summary>
public class SensitivePositionTests
{
    static ConstNode text = new("x", ClrTypeTag.String);

    // The walk driven by the server's resolver, asked directly for the shapes whose answer is not a
    // header — a fold over text that no database would run, or a model this fixture does not have rows
    // for. The same walk, the same resolver, one step short of the processor.
    static SensitiveSchema model = Resolver();

    static SensitiveSchema Resolver()
    {
        var options = new ScryOptions(typeof(TestContext));
        options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        return new(Schema.Build(options));
    }

    static BinaryNode Compared(params string[] path) =>
        new(BinaryOp.Equal, new MemberNode(path), text);

    static ProjectionMember Named(string name, params string[] path) =>
        new(name, new NodeValue(new MemberNode(path)));

    static TestDataRow<(QueryRequest Request, bool Returned)> Shape(QueryRequest request, bool returned, string name) =>
        new((request, returned), DisplayName: name);

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
        await using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Execute(request, context, fromUrl: true, out _));

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
        await using var context = TestContext.CreateSeeded();

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
        await using var context = TestContext.CreateSeeded();

        Execute(request, context, fromUrl: true, out var responseHeaders);

        await Assert.That(responseHeaders.CacheControl.ToString()).Contains("no-store");
    }

    // The largest of a marked member's values is one of them. Through the processor, since the rule
    // is the header: a fold that returns a value is a result that holds the member.
    [Test]
    public async Task TheLargestValueOfAMarkedMemberIsNotStorable()
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Invoice", [new AggregateOp(AggregateFn.Max, new MemberNode(["Reviewer"]))]);

        Execute(request, context, fromUrl: true, out var responseHeaders);

        await Assert.That(responseHeaders.CacheControl.ToString()).Contains("no-store");
    }

    // A fold that hands back one of the values — the smallest, the largest, every one of them joined —
    // returns the member as a Select would. A sum, an average or a count is a number of the fold's
    // own, and returns nothing of it.
    public static IEnumerable<TestDataRow<(QueryRequest Request, bool Returned)>> FoldedPositions()
    {
        yield return Shape(QueryRequest.Create("Invoice", [new AggregateOp(AggregateFn.Max, new MemberNode(["Reviewer"]))]), true, "a Max terminal");
        yield return Shape(QueryRequest.Create("Invoice", [new AggregateOp(AggregateFn.Min, new MemberNode(["Reviewer"]))]), true, "a Min terminal");
        yield return Shape(QueryRequest.Create("Invoice", [new AggregateOp(AggregateFn.Count, new MemberNode(["Reviewer"]))]), false, "a Count terminal");
        yield return Shape(Grouped(new(AggregateFn.Join, new MemberNode(["Reviewer"]), ", ")), true, "a Join in a grouped Select");
        yield return Shape(Grouped(new(AggregateFn.Max, new MemberNode(["Reviewer"]))), true, "a Max in a grouped Select");
        yield return Shape(Grouped(new(AggregateFn.Count, new MemberNode(["Reviewer"]))), false, "a Count in a grouped Select");
        yield return Shape(Grouped(new(AggregateFn.Sum, new MemberNode(["Reviewer"]))), false, "a Sum in a grouped Select");
        yield return Shape(Grouped(new(AggregateFn.Max, new MemberNode(["Number"]))), false, "a Max of an unmarked member in a grouped Select");
        yield return Shape(Selected(new SubqueryNode(["PreviousAddresses"], SubqueryFn.Max, null, new MemberNode(["City"]))), true, "a Max subquery in a Select");
        yield return Shape(Selected(new SubqueryNode(["PreviousAddresses"], SubqueryFn.Count)), false, "a Count subquery in a Select");
        yield return Shape(Selected(new SubqueryNode(["PreviousAddresses"], SubqueryFn.Any, Compared("City"))), false, "an Any subquery in a Select");
    }

    static QueryRequest Grouped(AggregateNode folded) =>
        QueryRequest.Create(
            "Invoice",
            [
                new GroupByOp([new MemberNode(["Number"])]),
                new SelectOp(new([new("Folded", new NodeValue(folded))]))
            ]);

    static QueryRequest Selected(Node value) =>
        QueryRequest.Create("Employee", [new SelectOp(new([new("Value", new NodeValue(value))]))]);

    [Test]
    [MethodDataSource(nameof(FoldedPositions))]
    public async Task AFoldReturnsTheMemberWhereItReturnsOneOfItsValues(QueryRequest request, bool returned)
    {
        var use = model.Inspect(request);

        await Assert.That(use.InProjection).IsEqualTo(returned);
    }

    // The subquery predicate above compared a constant against a member of a marked type, and that
    // part of the answer is unchanged by what the fold returns.
    [Test]
    public async Task AConstantInsideAFoldStillTravelsAsABody()
    {
        var use = model.Inspect(Selected(new SubqueryNode(["PreviousAddresses"], SubqueryFn.Count, Compared("City"))));

        await Assert.That(use.InConstant).IsTrue();
    }

    // A query with no Select returns the row it ends on, which is not always the root it began from: a
    // narrow changes it to the derived type, and a flatten to the collection's element. The shared
    // model has no hierarchy marking a derived member and no collection of a type marking one, so
    // these shapes are compiled here and asked of the walk through the server's resolver.
    static SensitiveSchema reshaped = Reshaped();

    static SensitiveSchema Reshaped()
    {
        var (assembly, _) = CompiledModel.Compile(
            """
            [Queryable] public class Plant { public int Id { get; set; } public string Name { get; set; } = ""; }
            [Queryable] public class Reactor : Plant { [Sensitive] public string Licence { get; set; } = ""; }
            [Queryable] public class Journal { public int Id { get; set; } [QueryableCollection] public List<Posting> Postings { get; set; } = new(); }
            [Queryable] public class Posting { public int Id { get; set; } [Sensitive] public string Memo { get; set; } = ""; }
            public sealed class ShapesContext : DbContext;
            """);
        return new(Schema.Build(new(assembly.GetType("ShapesContext")!)));
    }

    public static IEnumerable<TestDataRow<(QueryRequest Request, bool Returned)>> ReshapedRows()
    {
        yield return Shape(QueryRequest.Create("Plant", []), false, "the root alone, which marks nothing");
        yield return Shape(QueryRequest.Create("Plant", [new OfTypeOp("Reactor")]), true, "narrowed to a type marking a member, returned whole");
        yield return Shape(QueryRequest.Create("Plant", [new OfTypeOp("Reactor"), new SelectOp(new([Named("Name", "Name")]))]), false, "narrowed, with an unmarked member selected");
        yield return Shape(QueryRequest.Create("Plant", [new OfTypeOp("Reactor"), new SelectOp(new([Named("Licence", "Licence")]))]), true, "narrowed, with the marked member selected");
        yield return Shape(QueryRequest.Create("Journal", []), false, "the collection's owner alone, which marks nothing");
        yield return Shape(QueryRequest.Create("Journal", [new SelectManyOp(["Postings"])]), true, "flattened into elements marking a member, returned whole");
        yield return Shape(QueryRequest.Create("Journal", [new SelectManyOp(["Postings"]), new WhereOp(Compared("Id"))]), true, "flattened and filtered, returned whole");
        yield return Shape(QueryRequest.Create("Journal", [new SelectManyOp(["Postings"]), new SelectOp(new([Named("Id", "Id")]))]), false, "flattened, with an unmarked member selected");
        yield return Shape(QueryRequest.Create("Journal", [new SelectManyOp(["Postings"]), new CountOp()]), false, "flattened and counted");
    }

    [Test]
    [MethodDataSource(nameof(ReshapedRows))]
    public async Task ADefaultProjectionReadsTheRowThePipelineEndsOn(QueryRequest request, bool returned)
    {
        var use = reshaped.Inspect(request);

        await Assert.That(use.InProjection).IsEqualTo(returned);
    }

    // The walk runs before the validator, and must not fail on what the validator will reject: a key
    // index the grouping does not have, below as well as above, is a rejection rather than a fault —
    // and so is still audited, and fails one batch entry rather than the batch.
    [Test]
    public async Task AGroupKeyBelowZeroIsRejectedRatherThanFaulted()
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new GroupByOp([new MemberNode(["Workstation", "Extension"])]),
                new WhereOp(new BinaryNode(BinaryOp.Equal, new GroupKeyNode(-1), text)),
                new SelectOp(new([new("Extension", new NodeValue(new GroupKeyNode(0)))]))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Execute(request, context, fromUrl: false, out _));

        await Assert.That(exception.Message).Contains("out of range");
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
