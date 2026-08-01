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
            [new(StringComparer.Ordinal) { ["name"] = "Alice" }],
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
                        new BinaryNode(BinaryOp.Equal, new ElementNode(), new ConstNode("urgent", ClrTypeTag.String)),
                        null)),
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

        return Verify(string.Join("\n", json));
    }

    static Task VerifyRoundTrip(QueryRequest request)
    {
        var json = ScryJson.Serialize(request);
        var roundTripped = ScryJson.DeserializeRequest(json);
        var reserialized = ScryJson.Serialize(roundTripped);

        Assert.That(reserialized, Is.EqualTo(json));

        return Verify(json);
    }
}
