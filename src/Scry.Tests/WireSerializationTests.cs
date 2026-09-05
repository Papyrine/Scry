[TestFixture]
public class WireSerializationTests
{
    [Test]
    public Task FullPipelineRoundTrips()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.AndAlso,
                        new BinaryNode(
                            BinaryOp.Equal,
                            new MemberNode(["Status"]),
                            new ConstNode("FullTime", ClrTypeTag.Enum)),
                        new CallNode(
                            KnownFunction.StringStartsWith,
                            new MemberNode(["Name"]),
                            [new ConstNode("A", ClrTypeTag.String)]))),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new ThenByOp(new MemberNode(["Id"]), Descending: true),
                new SkipOp(10),
                new TakeOp(50),
                new SelectOp(
                    new(
                    [
                        new("Name", new NodeValue(new MemberNode(["Name"]))),
                        new("ManagerName", new NodeValue(new MemberNode(["Manager", "Name"]))),
                        new(
                            "Manager",
                            new NestedValue(
                                ["Manager"],
                                new([new("Name", new NodeValue(new MemberNode(["Name"])))])))
                    ]))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task GroupByAggregateRoundTrips()
    {
        var request = QueryRequest.Create(
            "Orders",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.GreaterThan,
                        new MemberNode(["Amount"]),
                        new ConstNode("0", ClrTypeTag.Decimal))),
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Region", new NodeValue(new MemberNode(["Region"]))),
                        new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"])))),
                        new("Count", new NodeValue(new AggregateNode(AggregateFn.Count, Selector: null)))
                    ]))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task TerminalsRoundTrip()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new UnaryNode(
                        UnaryOp.Not,
                        new CallNode(KnownFunction.StringIsNullOrEmpty, new MemberNode(["Name"]), []))),
                new FirstOp(OrDefault: true, new MemberNode(["Active"]))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task ByteArrayConstantRoundTrips()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Avatar"]),
                        new ConstNode(Convert.ToBase64String([0x01, 0x02, 0x03]), ClrTypeTag.Bytes)))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task PageTerminalRoundTrips()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new PageOp(20)
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task PageWithCursorRoundTrips()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new PageOp(20, "eyJrIjpbXX0.c2ln")
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public void PageEnvelopeResponseRoundTrips()
    {
        var page = new ScryPage<Dictionary<string, object?>>(
            [
                new(StringComparer.Ordinal)
                {
                    ["name"] = "Alice"
                }
            ],
            HasMore: true,
            Cursor: null);
        var json = ScryJson.Serialize(
            QueryResponse.Create(ResultKind.Page, JsonSerializer.SerializeToElement(page, ScryJson.Options)));

        // A null cursor is omitted from the wire, matching the fail-when-writing-null contract.
        Assert.That(json, Does.Not.Contain("cursor"));

        var response = ScryJson.DeserializeResponse(json);
        Assert.That(response.Kind, Is.EqualTo(ResultKind.Page));

        var roundTripped = response.Payload.Deserialize<ScryPage<Dictionary<string, object?>>>(ScryJson.Options)!;
        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.HasMore, Is.True);
            Assert.That(roundTripped.Cursor, Is.Null);
            Assert.That(roundTripped.Items, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void UnknownDiscriminatorFailsClosed()
    {
        var json =
            """
            {
              "version": 1,
              "root": "Employees",
              "pipeline": [
                {
                  "$type": "evil",
                  "predicate": null
                }
              ]
            }
            """;

        Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
    }

    [Test]
    public void MalformedJsonFailsClosed() =>
        Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest("{ not json"));

    // A member the vocabulary requires, left out: refused as malformed rather than read as its
    // default, which is a null the validator would dereference into a server fault.
    [Test]
    public void AnOmittedRequiredMemberFailsClosed()
    {
        var json =
            """
            {
              "version": 1,
              "root": "Employees",
              "pipeline": [
                {
                  "$type": "where"
                }
              ]
            }
            """;

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
        Assert.That(exception!.Message, Does.Contain("predicate"));
    }

    // The same member spelled as an explicit null: the absence's twin, and refused the same way.
    [Test]
    public void AnExplicitNullForARequiredMemberFailsClosed()
    {
        var json =
            """
            {
              "version": 1,
              "root": "Employees",
              "pipeline": [
                {
                  "$type": "where",
                  "predicate": null
                }
              ]
            }
            """;

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
        Assert.That(exception!.Message, Does.Contain("predicate"));
    }

    // A null element of a wire array is the absence's twin one level down: RespectNullableAnnotations
    // refuses a null member, and the element converter refuses these, so a validator never dereferences
    // one — which was a 500 recorded as Failed, for a request the client wrote.
    [TestCase("""{"version":1,"root":"Employees","pipeline":[null]}""", "QueryOp")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"groupBy","keys":[null]}]}""", "Node")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"where","predicate":{"$type":"call","function":"StringContains","target":{"$type":"member","path":"Name"},"arguments":[null]}}]}""", "Node")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"where","predicate":{"$type":"compositeKey","parts":[null]}}]}""", "Node")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"join","root":"Department","kind":"Inner","outerKey":{"$type":"member","path":"DepartmentId"},"innerKey":{"$type":"member","path":"Id"},"result":[null]}]}""", "JoinMember")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"join","root":"Department","kind":"Inner","outerKey":{"$type":"member","path":"DepartmentId"},"innerKey":{"$type":"member","path":"Id"},"result":[{"name":"Name","side":"Outer","path":"Name"}],"innerOps":[null]}]}""", "QueryOp")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"set","kind":"Union","root":"Department","projection":{"members":["Name"]},"operandOps":[null]}]}""", "QueryOp")]
    public void ANullArrayElementFailsClosed(string json, string element)
    {
        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
        Assert.That(exception!.Message, Does.Contain($"array of {element} cannot be null"));
    }

    [Test]
    public void ANullBatchEntryFailsClosed()
    {
        var exception = Assert.Throws<ScryWireException>(
            () => ScryJson.DeserializeBatchRequest("""{"version":1,"queries":[null]}"""));
        Assert.That(exception!.Message, Does.Contain("array of QueryRequest cannot be null"));
    }

    [Test]
    public void ANullAttachmentKeyFailsClosed()
    {
        var exception = Assert.Throws<ScryWireException>(
            () => ScryJson.DeserializeAttachmentRequest("""{"version":1,"root":"Employee","member":"Photo","keys":[null]}"""));
        Assert.That(exception!.Message, Does.Contain("array of AttachmentKey cannot be null"));
    }

    // A request names only what the vocabulary names: a member nothing reads is refused rather than
    // skipped, at every level. Skipping is what let a form field shaped as JSON carry its "=" in a
    // member the server never looked at.
    [TestCase("""{"version":1,"root":"Employees","pipeline":[],"pad":"="}""")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"count","extra":1}]}""")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"where","predicate":{"$type":"member","path":"Name","extra":1}}]}""")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"select","projection":{"members":[{"name":"N","value":{"$type":"node","node":{"$type":"member","path":"Name"}},"extra":1}]}}]}""")]
    [TestCase("""{"version":1,"root":"Employees","pipeline":[{"$type":"select","projection":{"members":["Name"],"extra":1}}]}""")]
    public void AnUnknownMemberFailsClosed(string json) =>
        Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));

    [Test]
    public void AnUnknownMemberOnABatchFailsClosed() =>
        Assert.Throws<ScryWireException>(
            () => ScryJson.DeserializeBatchRequest("""{"version":1,"queries":[],"pad":"="}"""));

    [Test]
    public void AnUnknownMemberOnAnAttachmentRequestFailsClosed() =>
        Assert.Throws<ScryWireException>(
            () => ScryJson.DeserializeAttachmentRequest("""{"version":1,"root":"Employee","member":"Photo","keys":[{"value":"1","tag":"Int32","pad":"="}]}"""));

    [Test]
    public void ANullRootFailsClosed()
    {
        var json =
            """
            {
              "version": 1,
              "root": null,
              "pipeline": []
            }
            """;

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
        Assert.That(exception!.Message, Does.Contain("root"));
    }

    [Test]
    public void AnOmittedRootFailsClosed()
    {
        var json =
            """
            {
              "version": 1,
              "pipeline": []
            }
            """;

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
        Assert.That(exception!.Message, Does.Contain("root"));
    }

    [Test]
    public void AnAttachmentRequestWithoutKeysFailsClosed()
    {
        var json =
            """
            {
              "version": 1,
              "root": "Employee",
              "member": "Photo"
            }
            """;

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeAttachmentRequest(json));
        Assert.That(exception!.Message, Does.Contain("keys"));
    }

    // The other half of that rule: every member the writer leaves out when null has to read back as
    // null, or a request this side wrote would be refused by the other. One of each such shape.
    [Test]
    public Task OmittedOptionalMembersReadBack()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Name"]),
                        new ConstNode(null, ClrTypeTag.String))),
                new WhereOp(new SubqueryNode(["Orders"], SubqueryFn.Any)),
                new WhereOp(new InSourceNode(new MemberNode(["Id"]), "Managers", new MemberNode(["Id"]))),
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    null,
                    [new("Name", JoinSide.Outer, ["Name"])]),
                new SetOp(SetKind.Union, "Contractors", null, new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new GroupByOp([new MemberNode(["Name"])]),
                new SelectOp(new([new("Total", new NodeValue(new AggregateNode(AggregateFn.Count)))])),
                new AnyOp(),
                new FirstOp(true),
                new LastOp(true),
                new SingleOp(true),
                new PageOp()
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public void AnAttachmentKeyWithoutAValueReadsBack()
    {
        var request = AttachmentRequest.Create("Employee", "Photo", [new(null, ClrTypeTag.String)]);

        var json = ScryJson.Serialize(request);
        var roundTripped = ScryJson.DeserializeAttachmentRequest(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("value"));
            Assert.That(roundTripped.Keys.Single().Value, Is.Null);
            Assert.That(roundTripped.Keys.Single().Tag, Is.EqualTo(ClrTypeTag.String));
        });
    }

    [Test]
    public void PathNamingOneMemberTravelsAsAString()
    {
        var request = QueryRequest.Create("Employees", [new WhereOp(new MemberNode(["Active"]))]);

        var json = ScryJson.Serialize(request);

        Assert.That(json, Does.Contain("""{"$type":"member","path":"Active"}"""));
    }

    [Test]
    public void PathNamingOneMemberAsAnArrayFailsClosed()
    {
        // The two spellings are alternatives, not synonyms: one member is a string and any other count
        // is an array, so a path has a single encoding and two requests meaning the same thing cannot
        // differ in bytes.
        var json =
            """
            {
              "version": 1,
              "root": "Employees",
              "pipeline": [
                {
                  "$type": "where",
                  "predicate": { "$type": "member", "path": ["Active"] }
                }
              ]
            }
            """;

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeRequest(json));
        Assert.That(exception!.Message, Does.Contain("written as a string"));
    }

    [Test]
    public void NewerResponseVersionFailsClosed()
    {
        var json =
            $$"""
              {
                "version": {{WireFormat.Version + 1}},
                "kind": "Scalar",
                "payload": 1
              }
              """;

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeResponse(json));
        Assert.That(exception!.Message, Does.Contain($"wire version {WireFormat.Version + 1}"));
    }

    [Test]
    public void CurrentResponseVersionIsAccepted()
    {
        var json = ScryJson.Serialize(QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(1)));

        var response = ScryJson.DeserializeResponse(json);
        Assert.That(response.Version, Is.EqualTo(WireFormat.Version));
    }

    [Test]
    public Task ExpandedExpressionsRoundTrip()
    {
        var request = QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.AndAlso,
                        new CallNode(
                            KnownFunction.In,
                            new MemberNode(["Status"]),
                            [new ConstNode("FullTime", ClrTypeTag.Enum), new ConstNode("PartTime", ClrTypeTag.Enum)]),
                        new BinaryNode(
                            BinaryOp.Equal,
                            new BinaryNode(
                                BinaryOp.Modulo,
                                new CallNode(KnownFunction.StringLength, new MemberNode(["Name"]), []),
                                new ConstNode("2", ClrTypeTag.Int32)),
                            new ConstNode("0", ClrTypeTag.Int32)))),
                new WhereOp(
                    new ConditionalNode(
                        new MemberNode(["Active"]),
                        new BinaryNode(
                            BinaryOp.Equal,
                            new BinaryNode(BinaryOp.Coalesce, new MemberNode(["ManagerId"]), new ConstNode("0", ClrTypeTag.Int32)),
                            new ConstNode("0", ClrTypeTag.Int32)),
                        new ConstNode("false", ClrTypeTag.Boolean))),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new DistinctOp()
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task ValueCollectionSubqueryRoundTrips()
    {
        // The element node's discriminator is part of the wire contract like every other: a server that
        // predates it fails the request at deserialization rather than reading it as something else.
        var request = QueryRequest.Create(
            "Orders",
            [
                new WhereOp(
                    new SubqueryNode(
                        ["Tags"],
                        SubqueryFn.Any,
                        new BinaryNode(BinaryOp.Equal, new ElementNode(), new ConstNode("urgent", ClrTypeTag.String)))),
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.GreaterThan,
                        new SubqueryNode(["Scores"], SubqueryFn.Sum, null, new ElementNode()),
                        new ConstNode("7", ClrTypeTag.Int32)))
            ]);

        return VerifyRoundTrip(request);
    }

    [Test]
    public Task ExpandedTerminalsRoundTrip()
    {
        // Each terminal is serialized on its own — a pipeline may only carry one — so this walks the
        // whole set through the discriminator map in a single assertion.
        QueryOp[] terminals =
        [
            new CountOp(new MemberNode(["Active"])),
            new LongCountOp(),
            new AllOp(new MemberNode(["Active"])),
            new LastOp(OrDefault: true, Predicate: null),
            new AggregateOp(AggregateFn.Sum, new MemberNode(["Id"])),
            new AggregateOp(AggregateFn.Average, new MemberNode(["Id"]))
        ];

        var json = terminals
            .Select(_ => ScryJson.Serialize(QueryRequest.Create("Employees", [_])))
            .ToList();

        foreach (var (serialized, index) in json.Select((_, i) => (_, i)))
        {
            var roundTripped = ScryJson.DeserializeRequest(serialized);
            Assert.That(ScryJson.Serialize(roundTripped), Is.EqualTo(serialized), $"terminal {index}");
        }

        return Verify(json)
            .Snapshot(
                """
                [
                  {"version":1,"root":"Employees","pipeline":[{"$type":"count","predicate":{"$type":"member","path":"Active"}}]},
                  {"version":1,"root":"Employees","pipeline":[{"$type":"longCount"}]},
                  {"version":1,"root":"Employees","pipeline":[{"$type":"all","predicate":{"$type":"member","path":"Active"}}]},
                  {"version":1,"root":"Employees","pipeline":[{"$type":"last","orDefault":true}]},
                  {"version":1,"root":"Employees","pipeline":[{"$type":"aggregate","function":"Sum","selector":{"$type":"member","path":"Id"}}]},
                  {"version":1,"root":"Employees","pipeline":[{"$type":"aggregate","function":"Average","selector":{"$type":"member","path":"Id"}}]}
                ]
                """);
    }

    [Test]
    public Task AttachmentRequestRoundTrips()
    {
        var request = AttachmentRequest.Create(
            "Employee",
            "Photo",
            [
                new("7", ClrTypeTag.Int32),
                new("a3f1c0de-0000-4000-8000-000000000001", ClrTypeTag.Guid)
            ],
            "SEJsUtm-XMA5VNZu");

        var json = ScryJson.Serialize(request);
        var roundTripped = ScryJson.DeserializeAttachmentRequest(json);

        Assert.That(ScryJson.Serialize(roundTripped), Is.EqualTo(json));

        return Verify(json)
            .Snapshot("{\"version\":1,\"root\":\"Employee\",\"member\":\"Photo\",\"keys\":[{\"value\":\"7\",\"tag\":\"Int32\"},{\"value\":\"a3f1c0de-0000-4000-8000-000000000001\",\"tag\":\"Guid\"}],\"stamp\":\"{scrubbed stamp}\"}");
    }

    [Test]
    public void MalformedAttachmentRequestFailsClosed() =>
        Assert.Throws<ScryWireException>(() => ScryJson.DeserializeAttachmentRequest("{ not json"));

    static Task VerifyRoundTrip(QueryRequest request)
    {
        var json = ScryJson.Serialize(request);
        var roundTripped = ScryJson.DeserializeRequest(json);
        var reserialized = ScryJson.Serialize(roundTripped);

        Assert.That(reserialized, Is.EqualTo(json));

        return Verify(json);
    }
}
