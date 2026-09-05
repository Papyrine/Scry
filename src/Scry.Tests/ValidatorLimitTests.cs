/// <summary>
/// The bounds the validator puts on a request's size and shape. Every one of these exists for input
/// nobody would write by hand — the client's own LINQ cannot produce a 33-operator pipeline or a
/// projection nested five deep — so each is reached the way an attacker would reach it, by building the
/// wire request directly. A limit that stops firing is not a wrong answer but an unbounded one, which
/// is why they are pinned by their message and by the number they name.
/// </summary>
[TestFixture]
public class ValidatorLimitTests
{
    [Test]
    public void ThePipelineLengthIsBounded()
    {
        // Counted before the pipeline is walked, so the cost of refusing is not the cost of validating.
        var ops = Enumerable.Repeat<QueryOp>(new WhereOp(new MemberNode(["Active"])), 33).ToList();

        Assert.That(Rejects("Employee", ops), Does.Contain("Pipeline exceeds the maximum length of 32"));
    }

    [Test]
    public void TheGroupByKeyCountIsBounded()
    {
        // A grouped row is materialized as a DistinctRow, which exists in arities up to eight. A ninth
        // key has no row type to land in, so it is refused rather than failing later without one.
        var keys = new Node[]
        {
            new MemberNode(["Id"]),
            new MemberNode(["Region"]),
            new MemberNode(["Amount"]),
            new MemberNode(["Quantity"]),
            new MemberNode(["Sku"]),
            new MemberNode(["Placed"]),
            new MemberNode(["Discount"]),
            new MemberNode(["Grade"]),
            new MemberNode(["Code"])
        };

        Assert.That(
            Rejects("Order", [new GroupByOp(keys)]),
            Does.Contain("GroupBy supports at most 8 keys"));
    }

    [Test]
    public void ProjectionNestingIsBounded()
    {
        // Employee.Manager is an Employee, so a nested projection can descend forever. Five levels is
        // one past MaxNavigationDepth.
        var projection = new Projection([new("Name", new NodeValue(new MemberNode(["Name"])))]);
        for (var i = 0; i < 5; i++)
        {
            projection = new([new("Manager", new NestedValue(["Manager"], projection))]);
        }

        Assert.That(
            Rejects("Employee", [new SelectOp(projection)]),
            Does.Contain("Projection nesting is too deep"));
    }

    [Test]
    public void AMemberPathIsBounded()
    {
        // The same self-navigation spelled as one path rather than as nesting: five segments where four
        // are allowed. Bounding only the nesting would leave this way down open.
        var path = new MemberNode(["Manager", "Manager", "Manager", "Manager", "Name"]);

        Assert.That(
            Rejects("Employee", [new OrderByOp(path, Descending: false)]),
            Does.Contain("path is too deep"));
    }

    [Test]
    public void ExpressionNestingIsBounded()
    {
        // A predicate wrapped in more negations than MaxExpressionDepth allows. The depth is checked at
        // the top of the walk, so the refusal costs the depth of the limit and not the depth of the
        // expression — which is the point of having one.
        var deep = Negated(40, new MemberNode(["Active"]));

        Assert.That(
            Rejects("Employee", [new WhereOp(deep)]),
            Does.Contain("Expression nesting is too deep"));
    }

    [Test]
    public void ExpressionNestingIsBoundedInAHavingClause()
    {
        // The Where that follows a GroupBy is validated by a different walk over the same node types,
        // with its own copy of the depth check. A bound on one is not a bound on the other.
        QueryOp[] ops =
        [
            new GroupByOp([new MemberNode(["Region"])]),
            new WhereOp(Negated(40, new MemberNode(["Active"]))),
            new SelectOp(new([new("Region", new NodeValue(new GroupKeyNode(0)))]))
        ];

        Assert.That(Rejects("Order", ops), Does.Contain("Expression nesting is too deep"));
    }

    // Neither depth nor width bounds a flat expression of thousands of nodes, so the count of them is
    // bounded on its own — across the whole request, since a per-operator budget is one the pipeline
    // length multiplies.
    [Test]
    public void TheExpressionNodeCountIsBounded()
    {
        // A balanced tree of sixteen comparisons is 31 binary nodes plus 32 leaves: wide, not deep.
        var wide = Balanced(16);

        Assert.That(
            Rejects("Employee", [new WhereOp(wide)], _ => _.MaxExpressionNodes = 40),
            Does.Contain("expression nodes, more than the maximum of 40"));
    }

    [Test]
    public void TheExpressionNodeCountIsCountedAcrossOperators()
    {
        // Each predicate alone is under the cap; the two together are not.
        QueryOp[] ops = [new WhereOp(Balanced(8)), new WhereOp(Balanced(8))];

        Assert.That(
            Rejects("Employee", ops, _ => _.MaxExpressionNodes = 40),
            Does.Contain("expression nodes"));
    }

    [Test]
    public void TheCorrelatedSubqueryCountIsBounded()
    {
        // Three questions about a collection side by side, where nesting one in another is refused
        // outright: each is a query the database runs per row.
        var lines = new SubqueryNode(["Lines"], SubqueryFn.Any);
        var predicate = new BinaryNode(BinaryOp.AndAlso, new BinaryNode(BinaryOp.AndAlso, lines, lines), lines);

        Assert.That(
            Rejects("Order", [new WhereOp(predicate)], _ => _.MaxCorrelatedSubqueries = 2),
            Does.Contain("correlated subqueries, more than the maximum of 2"));
    }

    [Test]
    public void AMembershipTestCountsAsACorrelatedSubquery()
    {
        var membership = new InSourceNode(new MemberNode(["Id"]), "Ticket", new MemberNode(["Id"]));
        var predicate = new BinaryNode(BinaryOp.AndAlso, membership, membership);

        Assert.That(
            Rejects("Employee", [new WhereOp(predicate)], _ => _.MaxCorrelatedSubqueries = 1),
            Does.Contain("correlated subqueries"));
    }

    static Node Balanced(int leaves)
    {
        if (leaves == 1)
        {
            return new BinaryNode(BinaryOp.Equal, new MemberNode(["Id"]), new ConstNode("1", ClrTypeTag.Int32));
        }

        return new BinaryNode(BinaryOp.OrElse, Balanced(leaves / 2), Balanced(leaves / 2));
    }

    [Test]
    public void ASetOperandCannotCarryEmptyOps()
    {
        // An operand carrying an empty list is not an operand without a filter — that is spelled by
        // leaving the ops off altogether — so it is refused rather than read as either.
        QueryOp[] operand = [];

        Assert.That(Rejects("Order", Union(operand)), Does.Contain("Empty ops on a set operand"));
    }

    [Test]
    public void ASetOperandSkipCannotBeNegative()
    {
        // A negative skip is not a smaller page but an unbounded one, and the operand's ops are the one
        // place paging arrives without having passed the top-level pipeline's own checks.
        QueryOp[] operand = [ordered, new SkipOp(-1)];

        Assert.That(Rejects("Order", Union(operand)), Does.Contain("Skip cannot be negative"));
    }

    [Test]
    public void ASetOperandTakeMustBeAtLeastOne()
    {
        QueryOp[] operand = [ordered, new TakeOp(0)];

        Assert.That(Rejects("Order", Union(operand)), Does.Contain("Take must be at least one"));
    }

    [Test]
    public void ASetOperandTakeIsBoundedByThePageSize()
    {
        // The page size caps an operand exactly as it caps the outer query. Without this a request
        // could ask for a bounded page of an unbounded operand.
        QueryOp[] operand = [ordered, new TakeOp(1001)];

        Assert.That(
            Rejects("Order", Union(operand)),
            Does.Contain("exceeds the maximum page size of 1000"));
    }

    // An ordering, because the side ops allow paging only where something bounds it.
    static readonly OrderByOp ordered = new(new MemberNode(["Price"]), Descending: false);

    static Node Negated(int count, Node inner)
    {
        var node = inner;
        for (var i = 0; i < count; i++)
        {
            node = new UnaryNode(UnaryOp.Not, node);
        }

        return node;
    }

    // Order unioned with OrderLine on a shape both project — the pair is valid, so validation reaches
    // the operand's own ops rather than stopping at the shapes.
    static QueryOp[] Union(IReadOnlyList<QueryOp> operandOps) =>
    [
        new SelectOp(new(
        [
            new("Name", new NodeValue(new MemberNode(["Region"]))),
            new("Value", new NodeValue(new MemberNode(["Amount"])))
        ])),
        new SetOp(
            SetKind.Union,
            "OrderLine",
            null,
            new(
            [
                new("Name", new NodeValue(new MemberNode(["Sku"]))),
                new("Value", new NodeValue(new MemberNode(["Price"])))
            ]))
        {
            OperandOps = operandOps
        }
    ];

    static string Rejects(string root, IReadOnlyList<QueryOp> pipeline, Action<ScryOptions>? extra = null)
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(root, pipeline);

        // Only a custom limit warrants a fresh processor; the default configuration is shared.
        var processor = extra is null
            ? SharedProcessor.Instance
            : ScryProcessor.Create<TestContext>(options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                extra(options);
            });

        var exception = Assert.Throws<ScryValidationException>(
            () => processor.Execute(request, context));

        return exception!.Message;
    }
}
