public class SecurityTests
{
    // begin-snippet: rejectIgnoredProperty
    [Test]
    public async Task RejectsIgnoredProperty() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.GreaterThan,
                    new MemberNode(["Salary"]),
                    new ConstNode("100", ClrTypeTag.Decimal)))
            ]));
    // end-snippet

    [Test]
    public async Task RejectsUnknownRoot() =>
        await AssertRejected(QueryRequest.Create("Secret", []));

    [Test]
    public async Task RejectsUnknownProperty() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Ssn"]), new ConstNode("x", ClrTypeTag.String)))]));

    [Test]
    public async Task RejectsTraversalThroughScalar() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Name", "Length"]), new ConstNode("3", ClrTypeTag.Int32)))]));

    // A [QueryIgnore] member of a complex type is hidden just like on an entity — traversing to it is
    // rejected, so a JSON column cannot smuggle in an unlisted field.
    [Test]
    public async Task RejectsIgnoredComplexMember() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Address", "Zip"]), new ConstNode("x", ClrTypeTag.String)))]));

    // A complex member is not a scalar; using it where a value is required is rejected (you must name
    // a scalar leaf such as Address.City).
    [Test]
    public async Task RejectsComplexMemberAsScalar() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Address"]), new ConstNode("x", ClrTypeTag.String)))]));

    // An attachment's value is never read by a query, so naming it anywhere is rejected — a generated
    // client cannot express it, which makes every request below a hand-built one.
    [Test]
    public async Task RejectsAttachmentInPredicate() =>
        await AssertRejected(QueryRequest.Create(
            "Contract",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Document"]), new ConstNode(null, ClrTypeTag.Null)))]));

    [Test]
    public async Task RejectsAttachmentInProjection() =>
        await AssertRejected(QueryRequest.Create(
            "Contract",
            [new SelectOp(new([new("Document", new NodeValue(new MemberNode(["Document"])))]))]));

    [Test]
    public async Task RejectsAttachmentInOrdering() =>
        await AssertRejected(QueryRequest.Create(
            "Contract",
            [new OrderByOp(new MemberNode(["Document"]), Descending: false)]));

    // Reached by traversing a navigation rather than named on the root, which is the path a validator
    // checking only the leaf would miss.
    [Test]
    public async Task RejectsAttachmentThroughNavigation() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Doc", new NodeValue(new MemberNode(["Manager", "Document"])))]))]));

    [Test]
    public async Task RejectsTakeOverMaxPageSize() =>
        await AssertRejected(
            QueryRequest.Create("Employee", [new TakeOp(50)]),
            options => options.MaxPageSize = 2);

    [Test]
    public async Task RejectsPageSizeOverMaxPageSize() =>
        await AssertRejected(
            QueryRequest.Create("Employee", [new PageOp(50)]),
            options => options.MaxPageSize = 2);

    [Test]
    public async Task RejectsInvalidPagingCursor() =>
        // Ordered query is seek-safe, so the server tries to decode the (garbage) cursor and rejects it.
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new OrderByOp(new MemberNode(["Name"]), false), new PageOp(2, "not-a-valid-cursor")]));

    [Test]
    public async Task RejectsCursorOnUnorderedQuery() =>
        // A cursor needs an ordering to resume; an unordered page with a cursor is rejected.
        await AssertRejected(QueryRequest.Create("Employee", [new PageOp(2, "anything")]));

    [Test]
    public async Task RejectsPagingGroupedQuery() =>
        await AssertRejected(QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])),
                new PageOp(10)
            ]));

    [Test]
    public async Task RejectsAggregateWithoutGroupBy() =>
        await AssertRejected(QueryRequest.Create(
            "Order",
            [new SelectOp(new([new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"]))))]))]));

    [Test]
    public async Task RejectsThenByWithoutOrderBy() =>
        await AssertRejected(QueryRequest.Create("Employee", [new ThenByOp(new MemberNode(["Name"]), false)]));

    [Test]
    public async Task RejectsOperatorAfterTerminal() =>
        await AssertRejected(QueryRequest.Create("Employee", [new CountOp(), new TakeOp(5)]));

    [Test]
    public async Task RejectsUnsupportedWireVersion() =>
        await AssertRejected(new(99, "Employee", []));

    [Test]
    public async Task RejectsGroupedProjectionReferencingNonKey() =>
        await AssertRejected(QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(new([new("Amount", new NodeValue(new MemberNode(["Amount"])))]))
            ]));

    // A function is validated for arity before anything is rebound, so a call the builder would read
    // more arguments from than were sent is a rejected query rather than a faulted one.
    [Test]
    public async Task RejectsFunctionWithMissingArgument() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new CallNode(KnownFunction.StringContains, new MemberNode(["Name"]), []))]));

    [Test]
    public async Task RejectsFunctionWithExtraArguments() =>
        await AssertRejected(QueryRequest.Create(
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
    public async Task RejectsDatePartOnNonTemporalMember() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.Equal,
                    new CallNode(KnownFunction.DateYear, new MemberNode(["Name"]), []),
                    new ConstNode("2026", ClrTypeTag.Int32)))
            ]));

    [Test]
    public async Task RejectsInSetOverTheConfiguredLimit() =>
        await AssertRejected(
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
    public async Task RejectsInSetContainingANonConstant() =>
        await AssertRejected(QueryRequest.Create(
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
    public async Task RejectsInSetOverTheConfiguredLimitInAGroupFilter() =>
        await AssertRejected(
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
    public async Task RejectsInSetContainingANonConstantInAGroupFilter() =>
        await AssertRejected(QueryRequest.Create(
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
    public async Task RejectsInSetOverTheConfiguredLimitInAGroupedProjection() =>
        await AssertRejected(
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
    public async Task RejectsJoinInnerPipelineOverTheConfiguredLength() =>
        await AssertRejected(
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
    public async Task RejectsSetOperandPipelineOverTheConfiguredLength() =>
        await AssertRejected(
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
    public async Task RejectsProjectionOverTheConfiguredWidth() =>
        await AssertRejected(
            QueryRequest.Create(
                "Employee",
                [new SelectOp(new([..Enumerable.Range(0, 5).Select(_ => NameMember($"Name{_}"))]))]),
            options => options.MaxProjectionMembers = 3,
            reason: "exceeds the maximum of 3 members");

    [Test]
    public async Task CountsNestedMembersTowardTheProjectionWidth() =>
        await AssertRejected(
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
    public async Task RejectsJoinResultOverTheConfiguredWidth() =>
        await AssertRejected(
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
    public async Task RejectsOrderByAfterDistinctOnAnUnprojectedMember() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new DistinctOp(),
                new OrderByOp(new MemberNode(["Status"]), Descending: false)
            ]));

    [Test]
    public async Task RejectsPagingAfterDistinct() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new DistinctOp(),
                new TakeOp(5)
            ]));

    // Ordering, paging and counting a deduplicated query materialize it as a row with one property per
    // projected member, so the arity is bounded. Beyond it the query can still be enumerated.
    [Test]
    public async Task RejectsCountingADistinctQueryBeyondTheRowArity() =>
        await AssertRejected(QueryRequest.Create(
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
    public async Task RejectsLastWithoutOrdering() =>
        await AssertRejected(QueryRequest.Create("Employee", [new LastOp(OrDefault: false, Predicate: null)]));

    [Test]
    public async Task RejectsAggregateTerminalOverAnIgnoredMember() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new AggregateOp(AggregateFn.Sum, new MemberNode(["Salary"]))]));

    [Test]
    public async Task RejectsAggregateTerminalAfterSelect() =>
        await AssertRejected(QueryRequest.Create(
            "Order",
            [
                new SelectOp(new([new("Amount", new NodeValue(new MemberNode(["Amount"])))])),
                new AggregateOp(AggregateFn.Sum, new MemberNode(["Amount"]))
            ]));

    // Count has its own terminal; carrying it as an aggregate would be a second spelling of the same
    // operation with a different result type.
    [Test]
    public async Task RejectsCountAsAnAggregateTerminal() =>
        await AssertRejected(QueryRequest.Create(
            "Order",
            [new AggregateOp(AggregateFn.Count, new MemberNode(["Amount"]))]));

    [Test]
    public async Task RejectsSummingANonNumericMember() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new AggregateOp(AggregateFn.Sum, new MemberNode(["Name"]))]));

    [Test]
    public async Task RejectsTerminalPredicateAfterSelect() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new CountOp(new MemberNode(["Active"]))
            ]));

    // A projection expression is one more place a row can be read from, not a place where more can be
    // read: the allow-list applies inside it exactly as it does inside a predicate.
    [Test]
    public async Task RejectsIgnoredPropertyInsideAProjectionExpression() =>
        await AssertRejected(QueryRequest.Create(
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
    public async Task RejectsNavigationAsAProjectionExpressionOperand() =>
        await AssertRejected(QueryRequest.Create(
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
    public async Task RejectsProjectionMemberThatReadsNothing() =>
        await AssertRejected(QueryRequest.Create(
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
    public async Task RejectionNamesTheWireNameNotTheClrType()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Region",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Nope"]), new ConstNode("x", ClrTypeTag.String)))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("on 'Region'");
            await Assert.That(exception.Message).DoesNotContain("SalesRegion");
        }
    }

    // Lookups are ordinal: a name is one spelling, on the source and on the member.
    [Test]
    public async Task RejectsASourceNameInAnotherCase() =>
        await AssertRejected(QueryRequest.Create("employee", [new CountOp()]));

    [Test]
    public async Task RejectsAMemberNameInAnotherCase() =>
        await AssertRejected(QueryRequest.Create("Employee", [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["name"]), new ConstNode("x", ClrTypeTag.String)))]));

    // A collation is a rule about text; over anything else it is a rejection, not a provider fault.
    [Test]
    public async Task RejectsACollationOverANonStringMember() =>
        await AssertRejected(QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new CollateNode(new MemberNode(["Id"]), StringMatch.CaseInsensitive), new ConstNode("1", ClrTypeTag.String)))]));

    // A comparison of two constants reads no member, so there is nothing for the allow-list to say
    // about it; it is accepted and answers what it says. Pinned as intended.
    [Test]
    public async Task AcceptsAComparisonOfTwoConstants()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new ConstNode("1", ClrTypeTag.Int32), new ConstNode("1", ClrTypeTag.Int32))), new CountOp()]);

        var response = SharedProcessor.Instance.Execute(request, context);

        await Assert.That(response.Payload.GetInt32()).IsGreaterThan(0);
    }

    // Below one is not an older contract but no contract: the first version was 1.
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task RejectsAWireVersionBelowOne(int version)
    {
        using var context = TestContext.CreateSeeded();
        var request = new QueryRequest(version, "Employee", [new CountOp()]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("Unsupported wire version");
    }

    // A name carried twice would shape a row whose later value silently overwrote the earlier one.
    [Test]
    public async Task RejectsAProjectionNamingAMemberTwice()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"]))), new("Name", new NodeValue(new MemberNode(["Id"])))]))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("'Name' is named more than once");
    }

    [Test]
    public async Task RejectsAJoinResultNamingAMemberTwice()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Name", JoinSide.Outer, ["Name"]), new("Name", JoinSide.Inner, ["Name"])])
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("'Name' is named more than once");
    }

    // Enum.Parse accepts any integer, and an undefined one would match nothing — or, for a flags
    // enum, match by bits nobody named. A constant is held to the values the enum defines.
    [Test]
    [Arguments("999")]
    [Arguments("-1")]
    public async Task RejectsAnEnumConstantSpelledAsAnUndefinedInteger(string value)
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Status"]), new ConstNode(value, ClrTypeTag.Enum)))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("is not a value of enum 'Status'");
    }

    // A constant index below zero addresses nothing and would be a provider fault; past the end is
    // the provider's to answer, since only it knows the row.
    [Test]
    [Arguments(KnownFunction.BytesElementAt, "Avatar", "-1")]
    [Arguments(KnownFunction.StringSubstring, "Name", "-1")]
    [Arguments(KnownFunction.StringSubstring, "Name", "0", "-1")]
    public async Task RejectsANegativeIndex(KnownFunction function, string member, params string[] indexes)
    {
        using var context = TestContext.CreateSeeded();
        var call = new CallNode(function, new MemberNode([member]), [.. indexes.Select(_ => new ConstNode(_, ClrTypeTag.Int32))]);
        var request = QueryRequest.Create("Employee", [new SelectOp(new([new("x", new NodeValue(call))]))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("cannot take a negative index");
    }

    // A rejection echoes what it rejected, which is the client's own text; a message is bounded so a
    // client cannot have its own megabytes handed back in the body, the audit trail, and the trace.
    [Test]
    [Arguments("root")]
    [Arguments("member")]
    [Arguments("constant")]
    public async Task ARejectionEchoingALongClientStringIsBounded(string where)
    {
        using var context = TestContext.CreateSeeded();
        var huge = new string('x', 100_000);
        var request = where switch
        {
            "root" => QueryRequest.Create(huge, [new CountOp()]),
            "member" => QueryRequest.Create("Employee", [new WhereOp(new MemberNode([huge]))]),
            _ => QueryRequest.Create(
                "Employee",
                [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Id"]), new ConstNode(huge, ClrTypeTag.Int32)))])
        };

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message.Length).IsLessThanOrEqualTo(ScryValidationException.MaxMessageLength + 1);
            await Assert.That(exception.Message).EndsWith("…");
        }
    }

    // The narrowing messages go through the same naming: a renamed root is named as the wire knows it.
    [Test]
    public async Task RejectionOnANarrowingNamesTheWireName()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Region", [new OfTypeOp("Employee")]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("'Region'");
            await Assert.That(exception.Message).DoesNotContain("SalesRegion");
        }
    }

    // A complex type has no source name. It is named the way introspection publishes it — the
    // generated model's name — which is what the allow-list already implies about it.
    [Test]
    public async Task RejectionOnAComplexTypeNamesItsModel()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Address", "Nope"]), new ConstNode("x", ClrTypeTag.String)))]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("on 'AddressQueryModel'");
    }

    // [QueryIgnore] on a base's virtual property, overridden without the attribute: hidden through
    // the override, on both sides — the generator carries every declaration's attributes onto the one
    // member, and the server reads the same chain.
    [Test]
    public async Task RejectsAnIgnoredBasePropertyThroughItsOverride() =>
        await AssertRejected(QueryRequest.Create(
            "Invoice",
            [new SelectOp(new([new("AuditTrail", new NodeValue(new MemberNode(["AuditTrail"])))]))]));

    // An enum reaches the wire by name, which the reader holds to the type, or as a number, which it
    // does not: the payload side needs numbers readable. So the validator holds every request enum to
    // its defined values, and one outside them is a rejection rather than a switch with no arm.
    [Test]
    [Arguments("""[{"$type":"select","projection":{"members":["Name"]}},{"$type":"set","kind":999,"root":"Department","projection":{"members":["Name"]}}]""", "Set kind")]
    [Arguments("""[{"$type":"where","predicate":{"$type":"binary","op":999,"left":{"$type":"member","path":"Id"},"right":{"$type":"const","value":"1","tag":"Int32"}}}]""", "Binary operator")]
    [Arguments("""[{"$type":"where","predicate":{"$type":"unary","op":999,"operand":{"$type":"member","path":"Active"}}}]""", "Unary operator")]
    [Arguments("""[{"$type":"where","predicate":{"$type":"binary","op":"Equal","left":{"$type":"member","path":"Id"},"right":{"$type":"const","value":"1","tag":999}}}]""", "Constant tag")]
    [Arguments("""[{"$type":"where","predicate":{"$type":"call","function":999,"target":{"$type":"member","path":"Name"},"arguments":[]}}]""", "Function")]
    [Arguments("""[{"$type":"where","predicate":{"$type":"collate","target":{"$type":"member","path":"Name"},"match":999}}]""", "String match")]
    [Arguments("""[{"$type":"join","root":"Department","kind":999,"outerKey":{"$type":"member","path":"DepartmentId"},"innerKey":{"$type":"member","path":"Id"},"result":[{"name":"Name","side":"Outer","path":"Name"}]}]""", "Join kind")]
    [Arguments("""[{"$type":"join","root":"Department","kind":"Inner","outerKey":{"$type":"member","path":"DepartmentId"},"innerKey":{"$type":"member","path":"Id"},"result":[{"name":"Name","side":999,"path":"Name"}]}]""", "Join side")]
    [Arguments("""[{"$type":"groupBy","keys":[{"$type":"member","path":"Name"}]},{"$type":"select","projection":{"members":[{"name":"C","value":{"$type":"node","node":{"$type":"aggregate","function":999,"selector":{"$type":"member","path":"Id"}}}}]}}]""", "Aggregate function")]
    public async Task RejectsAnUndefinedEnumValue(string pipeline, string what)
    {
        var request = ScryJson.DeserializeRequest($$"""{"version":1,"root":"Employee","pipeline":{{pipeline}}}""");

        await AssertRejected(request, reason: $"'{what}' has no value 999");
    }

    [Test]
    public async Task RejectsAnUndefinedSubqueryFunction()
    {
        var request = ScryJson.DeserializeRequest(
            """{"version":1,"root":"Order","pipeline":[{"$type":"where","predicate":{"$type":"subquery","path":"Lines","function":999}}]}""");

        await AssertRejected(request, reason: "'Subquery function' has no value 999");
    }

    // A reason pins which rule refused the request, for a shape more than one rule could have.
    static async Task AssertRejected(QueryRequest request, Action<ScryOptions>? extra = null, string? reason = null)
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

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => processor.Execute(request, context));
        if (reason is not null)
        {
            await Assert.That(exception!.Message).Contains(reason);
        }
    }
}
