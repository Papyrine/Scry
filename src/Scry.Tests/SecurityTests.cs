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

    // An attachment's value is never read by a query, so naming it anywhere is rejected — a generated
    // client cannot express it, which makes every request below a hand-built one.
    [Test]
    public void RejectsAttachmentInPredicate() =>
        AssertRejected(QueryRequest.Create(
            "Contract",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Document"]), new ConstNode(null, ClrTypeTag.Null)))]));

    [Test]
    public void RejectsAttachmentInProjection() =>
        AssertRejected(QueryRequest.Create(
            "Contract",
            [new SelectOp(new([new("Document", new NodeValue(new MemberNode(["Document"])))]))]));

    [Test]
    public void RejectsAttachmentInOrdering() =>
        AssertRejected(QueryRequest.Create(
            "Contract",
            [new OrderByOp(new MemberNode(["Document"]), Descending: false)]));

    // Reached by traversing a navigation rather than named on the root, which is the path a validator
    // checking only the leaf would miss.
    [Test]
    public void RejectsAttachmentThroughNavigation() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Doc", new NodeValue(new MemberNode(["Manager", "Document"])))]))]));

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

    // The same two rules hold over a group. A HAVING predicate and a grouped projection read a different
    // vocabulary from a row predicate, and the cap has to reach a call wherever one is written.
    [Test]
    public void RejectsInSetOverTheConfiguredLimitInAGroupFilter() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [
                    new GroupByOp([new MemberNode(["Name"])]),
                    new WhereOp(new CallNode(
                        KnownFunction.In,
                        new MemberNode(["Name"]),
                        [..Enumerable.Range(0, 5).Select(_ => new ConstNode(_.ToString(), ClrTypeTag.String))])),
                    new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
                ]),
            options => options.MaxInValues = 3,
            reason: "exceeds the maximum");

    [Test]
    public void RejectsInSetContainingANonConstantInAGroupFilter() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new GroupByOp([new MemberNode(["Name"])]),
                new WhereOp(new CallNode(
                    KnownFunction.In,
                    new MemberNode(["Name"]),
                    [new MemberNode(["Name"])])),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]),
            reason: "must be a constant");

    [Test]
    public void RejectsInSetOverTheConfiguredLimitInAGroupedProjection() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [
                    new GroupByOp([new MemberNode(["Name"])]),
                    new SelectOp(new(
                    [
                        new("Listed", new NodeValue(new CallNode(
                            KnownFunction.In,
                            new MemberNode(["Name"]),
                            [..Enumerable.Range(0, 5).Select(_ => new ConstNode(_.ToString(), ClrTypeTag.String))])))
                    ]))
                ]),
            options => options.MaxInValues = 3,
            reason: "exceeds the maximum");

    // A join's inner side and a set operand each carry a pipeline of their own, which the top-level
    // count never sees; each is held to the same length.
    [Test]
    public void RejectsJoinInnerPipelineOverTheConfiguredLength() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [
                    new JoinOp(
                        "Department",
                        JoinKind.Inner,
                        new MemberNode(["DepartmentId"]),
                        new MemberNode(["Id"]),
                        null,
                        [new("Name", JoinSide.Outer, ["Name"])])
                    {
                        InnerOps = [..Enumerable.Range(0, 5).Select(_ => InnerFilter())]
                    },
                    new CountOp()
                ]),
            options => options.MaxPipelineLength = 3,
            reason: "exceeds the maximum length");

    [Test]
    public void RejectsSetOperandPipelineOverTheConfiguredLength() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [
                    new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                    new SetOp(SetKind.Union, "Department", null, new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
                    {
                        OperandOps = [..Enumerable.Range(0, 5).Select(_ => InnerFilter())]
                    },
                    new CountOp()
                ]),
            options => options.MaxPipelineLength = 3,
            reason: "exceeds the maximum length");

    static WhereOp InnerFilter() =>
        new(new BinaryNode(BinaryOp.Equal, new MemberNode(["Id"]), new ConstNode("1", ClrTypeTag.Int32)));

    // Every projected member is an expression the provider compiles and a column the query returns,
    // so a projection's width is bounded like a pipeline's length — across nesting, and for a join.
    [Test]
    public void RejectsProjectionOverTheConfiguredWidth() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [new SelectOp(new([..Enumerable.Range(0, 5).Select(_ => NameMember($"Name{_}"))]))]),
            options => options.MaxProjectionMembers = 3,
            reason: "exceeds the maximum of 3 members");

    [Test]
    public void CountsNestedMembersTowardTheProjectionWidth() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [
                    new SelectOp(new(
                    [
                        NameMember("Name"),
                        new("Department", new NestedValue(
                            ["Department"],
                            new([..Enumerable.Range(0, 3).Select(_ => NameMember($"Name{_}"))])))
                    ]))
                ]),
            options => options.MaxProjectionMembers = 3,
            reason: "exceeds the maximum of 3 members");

    [Test]
    public void RejectsJoinResultOverTheConfiguredWidth() =>
        AssertRejected(
            QueryRequest.Create(
                "Employee",
                [
                    new JoinOp(
                        "Department",
                        JoinKind.Inner,
                        new MemberNode(["DepartmentId"]),
                        new MemberNode(["Id"]),
                        null,
                        [..Enumerable.Range(0, 5).Select(_ => new JoinMember($"Name{_}", JoinSide.Outer, ["Name"]))]),
                    new CountOp()
                ]),
            options => options.MaxProjectionMembers = 3,
            reason: "exceeds the maximum of 3");

    static ProjectionMember NameMember(string name) =>
        new(name, new NodeValue(new MemberNode(["Name"])));

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

    // Ordering, paging and counting a deduplicated query materialize it as a row with one property per
    // projected member, so the arity is bounded. Beyond it the query can still be enumerated.
    [Test]
    public void RejectsCountingADistinctQueryBeyondTheRowArity() =>
        AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new(
                [
                    new("A", new NodeValue(new MemberNode(["Id"]))),
                    new("B", new NodeValue(new MemberNode(["Name"]))),
                    new("C", new NodeValue(new MemberNode(["Status"]))),
                    new("D", new NodeValue(new MemberNode(["Active"]))),
                    new("E", new NodeValue(new MemberNode(["ManagerId"]))),
                    new("F", new NodeValue(new MemberNode(["DepartmentId"]))),
                    new("G", new NodeValue(new MemberNode(["Avatar"]))),
                    new("H", new NodeValue(new MemberNode(["Address", "City"]))),
                    new("I", new NodeValue(new MemberNode(["Address", "Country"])))
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

    // A rejection names the wire name — the one a Name override chose — never the CLR type behind it,
    // which the override may exist to keep off the wire.
    [Test]
    public void RejectionNamesTheWireNameNotTheClrType()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Region",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Nope"]), new ConstNode("x", ClrTypeTag.String)))]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain("on 'Region'"));
            Assert.That(exception.Message, Does.Not.Contain("SalesRegion"));
        });
    }

    // [QueryIgnore] on a base's virtual property, overridden without the attribute: hidden through
    // the override, on both sides — the generator carries every declaration's attributes onto the one
    // member, and the server reads the same chain.
    [Test]
    public void RejectsAnIgnoredBasePropertyThroughItsOverride() =>
        AssertRejected(QueryRequest.Create(
            "Invoice",
            [new SelectOp(new([new("AuditTrail", new NodeValue(new MemberNode(["AuditTrail"])))]))]));

    // An enum reaches the wire by name, which the reader holds to the type, or as a number, which it
    // does not: the payload side needs numbers readable. So the validator holds every request enum to
    // its defined values, and one outside them is a rejection rather than a switch with no arm.
    [TestCase("""[{"$type":"select","projection":{"members":["Name"]}},{"$type":"set","kind":999,"root":"Department","projection":{"members":["Name"]}}]""", "Set kind")]
    [TestCase("""[{"$type":"where","predicate":{"$type":"binary","op":999,"left":{"$type":"member","path":"Id"},"right":{"$type":"const","value":"1","tag":"Int32"}}}]""", "Binary operator")]
    [TestCase("""[{"$type":"where","predicate":{"$type":"unary","op":999,"operand":{"$type":"member","path":"Active"}}}]""", "Unary operator")]
    [TestCase("""[{"$type":"where","predicate":{"$type":"binary","op":"Equal","left":{"$type":"member","path":"Id"},"right":{"$type":"const","value":"1","tag":999}}}]""", "Constant tag")]
    [TestCase("""[{"$type":"where","predicate":{"$type":"call","function":999,"target":{"$type":"member","path":"Name"},"arguments":[]}}]""", "Function")]
    [TestCase("""[{"$type":"where","predicate":{"$type":"collate","target":{"$type":"member","path":"Name"},"match":999}}]""", "String match")]
    [TestCase("""[{"$type":"join","root":"Department","kind":999,"outerKey":{"$type":"member","path":"DepartmentId"},"innerKey":{"$type":"member","path":"Id"},"result":[{"name":"Name","side":"Outer","path":"Name"}]}]""", "Join kind")]
    [TestCase("""[{"$type":"join","root":"Department","kind":"Inner","outerKey":{"$type":"member","path":"DepartmentId"},"innerKey":{"$type":"member","path":"Id"},"result":[{"name":"Name","side":999,"path":"Name"}]}]""", "Join side")]
    [TestCase("""[{"$type":"groupBy","keys":[{"$type":"member","path":"Name"}]},{"$type":"select","projection":{"members":[{"name":"C","value":{"$type":"node","node":{"$type":"aggregate","function":999,"selector":{"$type":"member","path":"Id"}}}}]}}]""", "Aggregate function")]
    public void RejectsAnUndefinedEnumValue(string pipeline, string what)
    {
        var request = ScryJson.DeserializeRequest($$"""{"version":1,"root":"Employee","pipeline":{{pipeline}}}""");

        AssertRejected(request, reason: $"'{what}' has no value 999");
    }

    [Test]
    public void RejectsAnUndefinedSubqueryFunction()
    {
        var request = ScryJson.DeserializeRequest(
            """{"version":1,"root":"Order","pipeline":[{"$type":"where","predicate":{"$type":"subquery","path":"Lines","function":999}}]}""");

        AssertRejected(request, reason: "'Subquery function' has no value 999");
    }

    // A reason pins which rule refused the request, for a shape more than one rule could have.
    static void AssertRejected(QueryRequest request, Action<ScryOptions>? extra = null, string? reason = null)
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

        var exception = Assert.Throws<ScryValidationException>(() => processor.Execute(request, context));
        if (reason is not null)
        {
            Assert.That(exception!.Message, Does.Contain(reason));
        }
    }
}
