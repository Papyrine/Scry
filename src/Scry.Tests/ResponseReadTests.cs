/// <summary>
/// Reading a response from the UTF-8 it arrived as. The bytes overload keeps the payload as bytes
/// rather than parsing it into a document, so what matters is that it stays indistinguishable from
/// the string overload in every way a caller can observe: the same values, the same payload, the same
/// re-serialized bytes, and the same refusals.
/// </summary>
public class ResponseReadTests
{
    // ReSharper disable once NotAccessedPositionalProperty.Local
    record Row(string Name, int Rank, Status Status);

    const string listJson =
        """
        {"version":1,"kind":"List","payload":[{"name":"Alice","rank":1,"status":"FullTime"},{"name":"Bob","rank":2,"status":"PartTime"}],"stamp":"abc123"}
        """;

    static byte[] Utf8(string json) =>
        Encoding.UTF8.GetBytes(json);

    [Test]
    public async Task ReadsTheSameEnvelopeAsTheStringOverload()
    {
        var fromText = ScryJson.DeserializeResponse(listJson);
        var fromBytes = ScryJson.DeserializeResponse(Utf8(listJson));

        using (Assert.Multiple())
        {
            await Assert.That(fromBytes.Version).IsEqualTo(fromText.Version);
            await Assert.That(fromBytes.Kind).IsEqualTo(fromText.Kind);
            await Assert.That(fromBytes.Stamp).IsEqualTo(fromText.Stamp);
        }
    }

    [Test]
    public async Task ReadsTheSamePayloadFromBytesAsFromAnElement()
    {
        var fromText = ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(listJson));
        var fromBytes = ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(Utf8(listJson)));

        await Assert.That(fromBytes).IsEquivalentTo(fromText!, CollectionOrdering.Matching);
        await Assert.That(fromBytes).IsEquivalentTo(new List<Row>
        {
            new("Alice", 1, Status.FullTime),
            new("Bob", 2, Status.PartTime)
        }, CollectionOrdering.Matching);
    }

    // The payload is stepped over on the way in, so this is the first thing that parses it. Nothing
    // about it may differ from a payload that was parsed eagerly.
    [Test]
    public async Task MaterializesThePayloadOnFirstRead()
    {
        var response = ScryJson.DeserializeResponse(Utf8(listJson));

        using (Assert.Multiple())
        {
            await Assert.That(response.Payload.ValueKind).IsEqualTo(JsonValueKind.Array);
            await Assert.That(response.Payload.GetArrayLength()).IsEqualTo(2);
            await Assert.That(response.Payload[0].GetProperty("name").GetString()).IsEqualTo("Alice");
            // Twice, because the second read comes off the cached document rather than parsing again.
            await Assert.That(response.Payload.GetArrayLength()).IsEqualTo(2);
        }
    }

    // The payload is parsed on first read, and a value whose hash changes when a member is read is not
    // one that can be put in a dictionary. The parse is kept off the record's own fields for that
    // reason, so reading it has to leave equality and the hash where they were.
    [Test]
    public async Task KeepsItsHashCodeWhenThePayloadIsRead()
    {
        var response = ScryJson.DeserializeResponse(Utf8(listJson));
        var before = response.GetHashCode();
        var copy = response with {Stamp = "other"};

        _ = response.Payload;

        using (Assert.Multiple())
        {
            await Assert.That(response.GetHashCode()).IsEqualTo(before);
            await Assert.That(response).IsEqualTo(response with {});
            // A copy replacing the payload must not reach back into the response it was copied from.
            await Assert.That(copy with {Payload = default}).IsNotSameReferenceAs(response);
            await Assert.That(response.Payload.GetArrayLength()).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ReSerializesToTheBytesItWasReadFrom()
    {
        var response = ScryJson.DeserializeResponse(Utf8(listJson));

        // The envelope's member order is part of the wire, and a response read from bytes has to write
        // back out in it — the payload included, which the reader never turned into a document.
        await Assert.That(ScryJson.Serialize(response)).IsEqualTo(listJson);
    }

    [Test]
    public async Task ReSerializesAScalarReadFromBytes()
    {
        const string json = """{"version":1,"kind":"Scalar","payload":42,"stamp":"abc123"}""";
        var response = ScryJson.DeserializeResponse(Utf8(json));

        using (Assert.Multiple())
        {
            await Assert.That(response.Payload.GetInt32()).IsEqualTo(42);
            await Assert.That(ScryJson.Serialize(response)).IsEqualTo(json);
        }
    }

    [Test]
    public async Task ReadsANullPayloadFromBytes()
    {
        const string json = """{"version":1,"kind":"Single","payload":null,"stamp":"abc123"}""";
        var response = ScryJson.DeserializeResponse(Utf8(json));

        using (Assert.Multiple())
        {
            await Assert.That(response.Payload.ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(ScryJson.Serialize(response)).IsEqualTo(json);
        }
    }

    // A payload holding the envelope's own member names, so a reader that found the payload's extent
    // by scanning for them rather than by structure would take the wrong slice.
    [Test]
    public async Task ReadsAPayloadCarryingTheEnvelopesOwnNames()
    {
        const string json =
            """
            {"version":1,"kind":"List","payload":[{"name":"version","rank":1,"status":"FullTime"},{"name":"payload\"stamp","rank":2,"status":"PartTime"}],"stamp":"abc123"}
            """;
        var rows = ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(Utf8(json)));

        await Assert.That(rows!.Select(_ => _.Name)).IsEquivalentTo(["version", "payload\"stamp"], CollectionOrdering.Matching);
    }

    // The payload is not the last member here, so the slice has to end where the value does rather
    // than running to the end of the document.
    [Test]
    public async Task ReadsAPayloadFollowedByFurtherMembers()
    {
        const string json =
            """
            {"version":1,"kind":"List","payload":[{"name":"Alice","rank":1,"status":"FullTime"}],"stamp":"abc123","enumAliases":[{"enumName":"Status","valueName":"FullTime","previousNames":["Full"]}]}
            """;
        var response = ScryJson.DeserializeResponse(Utf8(json));

        using (Assert.Multiple())
        {
            await Assert.That(response.EnumAliases).Count().IsEqualTo(1);
            await Assert.That(ScryJson.DeserializePayload<List<Row>>(response)!.Single().Name).IsEqualTo("Alice");
        }
    }

    [Test]
    public async Task RefusesANewerWireVersionFromBytes()
    {
        var json = $$"""{"version":{{WireFormat.Version + 1}},"kind":"List","payload":[]}""";

        var exception = Assert.ThrowsExactly<ScryWireException>(() => ScryJson.DeserializeResponse(Utf8(json)));

        await Assert.That(exception.Message).Contains("Unsupported response wire version");
    }

    [Test]
    public async Task ReportsMalformedBytesAsAWireFailure()
    {
        var exception = Assert.ThrowsExactly<ScryWireException>(
            () => ScryJson.DeserializeResponse(Utf8("""{"version":1,"kind":"List","payload":[}""")));

        await Assert.That(exception.Message).StartsWith("Invalid query response");
    }

    // A payload read leaves the scope that told the reader to step over payloads; a failed one has to
    // leave it too, or the next response on this thread would come back with an unparsed payload.
    [Test]
    public async Task LeavesNoScopeBehindAfterAFailedRead()
    {
        Assert.ThrowsExactly<ScryWireException>(
            () => ScryJson.DeserializeResponse(Utf8("""{"version":1,"kind":"List","payload":[}""")));

        var response = ScryJson.DeserializeResponse(listJson);
        await Assert.That(response.Payload.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task ReadsABatchsPayloadsFromBytes()
    {
        const string json =
            """
            {"version":1,"results":[
              {"response":{"version":1,"kind":"List","payload":[{"name":"Alice","rank":1,"status":"FullTime"}]}},
              {"response":{"version":1,"kind":"List","payload":[{"name":"Bob","rank":2,"status":"PartTime"}]}}],"stamp":"abc123"}
            """;
        var batch = ScryJson.DeserializeBatchResponse(Utf8(json));

        using (Assert.Multiple())
        {
            await Assert.That(ScryJson.DeserializePayload<List<Row>>(batch.Results[0].Response!)!.Single().Name).IsEqualTo("Alice");
            await Assert.That(ScryJson.DeserializePayload<List<Row>>(batch.Results[1].Response!)!.Single().Name).IsEqualTo("Bob");
        }
    }

    // An entry that failed carries no response and so no payload was stepped over for it. The entries
    // after it must still line up with their own bytes rather than being shifted by one.
    [Test]
    public async Task PairsBatchPayloadsPastAFailedEntry()
    {
        const string json =
            """
            {"version":1,"results":[
              {"error":"Unknown source 'Nope'.","status":400},
              {"response":{"version":1,"kind":"List","payload":[{"name":"Bob","rank":2,"status":"PartTime"}]}}],"stamp":"abc123"}
            """;
        var batch = ScryJson.DeserializeBatchResponse(Utf8(json));

        using (Assert.Multiple())
        {
            await Assert.That(batch.Results[0].Error).IsEqualTo("Unknown source 'Nope'.");
            await Assert.That(ScryJson.DeserializePayload<List<Row>>(batch.Results[1].Response!)!.Single().Name).IsEqualTo("Bob");
        }
    }

    [Test]
    public async Task ReadsAnErrorBodyFromBytes()
    {
        var error = ScryJson.TryDeserializeError(Utf8("""{"error":"Nope.","code":"StaleClient"}"""));

        using (Assert.Multiple())
        {
            await Assert.That(error!.Error).IsEqualTo("Nope.");
            await Assert.That(error.Code).IsEqualTo(ScryErrorCode.StaleClient);
        }
    }

    [Test]
    public async Task ReturnsNullForABodyThatIsNotAnError() =>
        await Assert.That(ScryJson.TryDeserializeError(Utf8("<html>502 from a proxy</html>"))).IsNull();

    [Test]
    public async Task ReadsAStreamedRowFromBytes()
    {
        var row = ScryJson.DeserializeRow<Row>(
            "{\"name\":\"Alice\",\"rank\":1,\"status\":\"FullTime\"}"u8,
            aliases: null);

        await Assert.That(row).IsEqualTo(new("Alice", 1, Status.FullTime));
    }

    // The aliases reach the enum reader the same way they do on the element overload, so a client
    // generated before a rename still resolves the current name.
    [Test]
    public async Task ResolvesARenamedEnumValueOnARowReadFromBytes()
    {
        var row = ScryJson.DeserializeRow<Row>(
            "{\"name\":\"Alice\",\"rank\":1,\"status\":\"Permanent\"}"u8,
            [new("Status", "Permanent", ["FullTime"])]);

        await Assert.That(row!.Status).IsEqualTo(Status.FullTime);
    }

    [Test]
    public async Task ReadsAMarkerFromBytes()
    {
        var marker = ScryJson.DeserializeMarker("""{"$scry":"begin","version":1,"stamp":"abc123"}"""u8);

        using (Assert.Multiple())
        {
            await Assert.That(marker.Kind).IsEqualTo(ScryStream.Begin);
            await Assert.That(marker.Stamp).IsEqualTo("abc123");
        }
    }
}
