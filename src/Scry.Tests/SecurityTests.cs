[TestFixture]
public class SecurityTests
{
    // begin-snippet: rejectIgnoredProperty
    [Test]
    public void RejectsIgnoredProperty() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.GreaterThan,
                    new MemberNode(["Salary"]),
                    new ConstNode("100", ClrTypeTag.Decimal)))
            ]));
    // end-snippet

    [Test]
    public void RejectsUnknownRoot() =>
        AssertRejected(QueryRequest.Create("Secret", []));

    [Test]
    public void RejectsUnknownProperty() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Ssn"]), new ConstNode("x", ClrTypeTag.String)))]));

    [Test]
    public void RejectsTraversalThroughScalar() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Name", "Length"]), new ConstNode("3", ClrTypeTag.Int32)))]));

    // A [QueryIgnore] member of a complex type is hidden just like on an entity — traversing to it is
    // rejected, so a JSON column cannot smuggle in an unlisted field.
    [Test]
    public void RejectsIgnoredComplexMember() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Address", "Zip"]), new ConstNode("x", ClrTypeTag.String)))]));

    // A complex member is not a scalar; using it where a value is required is rejected (you must name
    // a scalar leaf such as Address.City).
    [Test]
    public void RejectsComplexMemberAsScalar() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Address"]), new ConstNode("x", ClrTypeTag.String)))]));

    [Test]
    public void RejectsTakeOverMaxPageSize() =>
        AssertRejected(
            QueryRequest.Create("Employee", [new TakeOp(50)]),
            options => options.MaxPageSize = 2);

    [Test]
    public void RejectsPageSizeOverMaxPageSize() =>
        AssertRejected(
            QueryRequest.Create("Employee", [new PageOp(50)]),
            options => options.MaxPageSize = 2);

    [Test]
    public void RejectsInvalidPagingCursor() =>
        // Ordered query is seek-safe, so the server tries to decode the (garbage) cursor and rejects it.
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new OrderByOp(new MemberNode(["Name"]), false), new PageOp(2, "not-a-valid-cursor")]));

    [Test]
    public void RejectsCursorOnUnorderedQuery() =>
        // A cursor needs an ordering to resume; an unordered page with a cursor is rejected.
        AssertRejected(QueryRequest.Create("Employee", [new PageOp(2, "anything")]));

    [Test]
    public void RejectsPagingGroupedQuery() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])),
                new PageOp(10)
            ]));

    [Test]
    public void RejectsAggregateWithoutGroupBy() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"]))))]))]));

    [Test]
    public void RejectsThenByWithoutOrderBy() =>
        AssertRejected(QueryRequest.Create("Employee", [new ThenByOp(new MemberNode(["Name"]), false)]));

    [Test]
    public void RejectsOperatorAfterTerminal() =>
        AssertRejected(QueryRequest.Create("Employee", [new CountOp(), new TakeOp(5)]));

    [Test]
    public void RejectsUnsupportedWireVersion() =>
        AssertRejected(new(99, "Employee", []));

    [Test]
    public void RejectsGroupedProjectionReferencingNonKey() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Amount", new NodeValue(new MemberNode(["Amount"])))]))
            ]));

    // A function is validated for arity before anything is rebound, so a call the builder would read
    // more arguments from than were sent is a rejected query rather than a faulted one.
    [Test]
    public void RejectsFunctionWithMissingArgument() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new CallNode(KnownFunction.StringContains, new MemberNode(["Name"]), []))]));

    [Test]
    public void RejectsFunctionWithExtraArguments() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new CallNode(
                    KnownFunction.StringStartsWith,
                    new MemberNode(["Name"]),
                    [new ConstNode("a", ClrTypeTag.String), new ConstNode("b", ClrTypeTag.String)]))
            ]));

    // A date part applied to a member that has none cannot be rebound; it is reported as a rejection
    // rather than surfacing as a server fault.
    [Test]
    public void RejectsDatePartOnNonTemporalMember() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.Equal,
                    new CallNode(KnownFunction.DateYear, new MemberNode(["Name"]), []),
                    new ConstNode("2026", ClrTypeTag.Int32)))
            ]));

    [Test]
    public void RejectsInSetOverTheConfiguredLimit() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [
                    new WhereOp(new CallNode(
                        KnownFunction.In,
                        new MemberNode(["Name"]),
                        [..Enumerable.Range(0, 5).Select(_ => new ConstNode(_.ToString(), ClrTypeTag.String))]))
                ]),
            options => options.MaxInValues = 3);

    // Every candidate value must be a literal: a member node here would be comparing the row against
    // itself through a path that was never validated as a set.
    [Test]
    public void RejectsInSetContainingANonConstant() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new CallNode(
                    KnownFunction.In,
                    new MemberNode(["Name"]),
                    [new MemberNode(["Address", "City"])]))
            ]));

    // Ordering a deduplicated query is allowed, but only by the member it deduplicated: every other
    // column was folded away, so naming one would order by something the rows no longer carry.
    [Test]
    public void RejectsOrderByAfterDistinctOnAnUnprojectedMember() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new DistinctOp(),
                new OrderByOp(new MemberNode(["Status"]), Descending: false)
            ]));

    [Test]
    public void RejectsPagingAfterDistinct() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new DistinctOp(),
                new TakeOp(5)
            ]));

    [Test]
    public void RejectsCountingADistinctQueryOverSeveralMembers() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new(
                [
                    new("Name", new NodeValue(new MemberNode(["Name"]))),
                    new("Status", new NodeValue(new MemberNode(["Status"])))
                ])),
                new DistinctOp(),
                new CountOp()
            ]));

    [Test]
    public void RejectsLastWithoutOrdering() =>
        AssertRejected(QueryRequest.Create("Employee", [new LastOp(OrDefault: false, Predicate: null)]));

    [Test]
    public void RejectsAggregateTerminalOverAnIgnoredMember() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new AggregateOp(AggregateFn.Sum, new MemberNode(["Salary"]))]));

    [Test]
    public void RejectsAggregateTerminalAfterSelect() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [
                new SelectOp(new([new("Amount", new NodeValue(new MemberNode(["Amount"])))])),
                new AggregateOp(AggregateFn.Sum, new MemberNode(["Amount"]))
            ]));

    // Count has its own terminal; carrying it as an aggregate would be a second spelling of the same
    // operation with a different result type.
    [Test]
    public void RejectsCountAsAnAggregateTerminal() =>
        AssertRejected(QueryRequest.Create(
            "Order",
            [new AggregateOp(AggregateFn.Count, new MemberNode(["Amount"]))]));

    [Test]
    public void RejectsSummingANonNumericMember() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new AggregateOp(AggregateFn.Sum, new MemberNode(["Name"]))]));

    [Test]
    public void RejectsTerminalPredicateAfterSelect() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new CountOp(new MemberNode(["Active"]))
            ]));

    // A projection expression is one more place a row can be read from, not a place where more can be
    // read: the allow-list applies inside it exactly as it does inside a predicate.
    [Test]
    public void RejectsIgnoredPropertyInsideAProjectionExpression() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new(
                [
                    new("Doubled", new NodeValue(new BinaryNode(
                        BinaryOp.Multiply,
                        new MemberNode(["Salary"]),
                        new ConstNode("2", ClrTypeTag.Decimal))))
                ]))
            ]));

    [Test]
    public void RejectsNavigationAsAProjectionExpressionOperand() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new(
                [
                    new("Upper", new NodeValue(new CallNode(
                        KnownFunction.StringToUpper,
                        new MemberNode(["Department"]),
                        [])))
                ]))
            ]));

    [Test]
    public void RejectsProjectionMemberThatReadsNothing() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new(
                [
                    new("Fixed", new NodeValue(new ConstNode("x", ClrTypeTag.String)))
                ]))
            ]));

    static void AssertRejected(QueryRequest request, Action<ScryOptions>? extra = null)
    {
        using var context = TestContext.CreateSeeded();
        // Only a custom limit warrants a fresh processor; the default configuration is shared.
        var processor = extra is null
            ? SharedProcessor.Instance
            : ScryProcessor.Create<TestContext>(options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                extra(options);
            });

        Assert.Throws<ScryValidationException>(() => processor.Execute(request, context));
    }
}
