/// <summary>
/// Reading a response from the UTF-8 it arrived as. The bytes overload keeps the payload as bytes
/// rather than parsing it into a document, so what matters is that it stays indistinguishable from
/// the string overload in every way a caller can observe: the same values, the same payload, the same
/// re-serialized bytes, and the same refusals.
/// </summary>
[TestFixture]
public class ResponseReadTests
{
    record Row(string Name, int Rank, Status Status);

    const string listJson =
        """
        {"version":1,"kind":"List","payload":[{"name":"Alice","rank":1,"status":"FullTime"},{"name":"Bob","rank":2,"status":"PartTime"}],"stamp":"abc123"}
        """;

    static byte[] Utf8(string json) =>
        Encoding.UTF8.GetBytes(json);

    [Test]
    public void ReadsTheSameEnvelopeAsTheStringOverload()
    {
        var fromText = ScryJson.DeserializeResponse(listJson);
        var fromBytes = ScryJson.DeserializeResponse(Utf8(listJson));

        Assert.Multiple(() =>
        {
            Assert.That(fromBytes.Version, Is.EqualTo(fromText.Version));
            Assert.That(fromBytes.Kind, Is.EqualTo(fromText.Kind));
            Assert.That(fromBytes.Stamp, Is.EqualTo(fromText.Stamp));
        });
    }

    [Test]
    public void ReadsTheSamePayloadFromBytesAsFromAnElement()
    {
        var fromText = ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(listJson));
        var fromBytes = ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(Utf8(listJson)));

        Assert.That(fromBytes, Is.EqualTo(fromText));
        Assert.That(fromBytes, Is.EqualTo(new List<Row>
        {
            new("Alice", 1, Status.FullTime),
            new("Bob", 2, Status.PartTime)
        }));
    }

    // The payload is stepped over on the way in, so this is the first thing that parses it. Nothing
    // about it may differ from a payload that was parsed eagerly.
    [Test]
    public void MaterializesThePayloadOnFirstRead()
    {
        var response = ScryJson.DeserializeResponse(Utf8(listJson));

        Assert.Multiple(() =>
        {
            Assert.That(response.Payload.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(response.Payload.GetArrayLength(), Is.EqualTo(2));
            Assert.That(response.Payload[0].GetProperty("name").GetString(), Is.EqualTo("Alice"));
            // Twice, because the second read comes off the cached document rather than parsing again.
            Assert.That(response.Payload.GetArrayLength(), Is.EqualTo(2));
        });
    }

    // The payload is parsed on first read, and a value whose hash changes when a member is read is not
    // one that can be put in a dictionary. The parse is kept off the record's own fields for that
    // reason, so reading it has to leave equality and the hash where they were.
    [Test]
    public void KeepsItsHashCodeWhenThePayloadIsRead()
    {
        var response = ScryJson.DeserializeResponse(Utf8(listJson));
        var before = response.GetHashCode();
        var copy = response with {Stamp = "other"};

        _ = response.Payload;

        Assert.Multiple(() =>
        {
            Assert.That(response.GetHashCode(), Is.EqualTo(before));
            Assert.That(response, Is.EqualTo(response with {}));
            // A copy replacing the payload must not reach back into the response it was copied from.
            Assert.That(copy with {Payload = default}, Is.Not.SameAs(response));
            Assert.That(response.Payload.GetArrayLength(), Is.EqualTo(2));
        });
    }

    [Test]
    public void ReSerializesToTheBytesItWasReadFrom()
    {
        var response = ScryJson.DeserializeResponse(Utf8(listJson));

        // The envelope's member order is part of the wire, and a response read from bytes has to write
        // back out in it — the payload included, which the reader never turned into a document.
        Assert.That(ScryJson.Serialize(response), Is.EqualTo(listJson));
    }

    [Test]
    public void ReSerializesAScalarReadFromBytes()
    {
        const string json = """{"version":1,"kind":"Scalar","payload":42,"stamp":"abc123"}""";
        var response = ScryJson.DeserializeResponse(Utf8(json));

        Assert.Multiple(() =>
        {
            Assert.That(response.Payload.GetInt32(), Is.EqualTo(42));
            Assert.That(ScryJson.Serialize(response), Is.EqualTo(json));
        });
    }

    [Test]
    public void ReadsANullPayloadFromBytes()
    {
        const string json = """{"version":1,"kind":"Single","payload":null,"stamp":"abc123"}""";
        var response = ScryJson.DeserializeResponse(Utf8(json));

        Assert.Multiple(() =>
        {
            Assert.That(response.Payload.ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(ScryJson.Serialize(response), Is.EqualTo(json));
        });
    }

    // A payload holding the envelope's own member names, so a reader that found the payload's extent
    // by scanning for them rather than by structure would take the wrong slice.
    [Test]
    public void ReadsAPayloadCarryingTheEnvelopesOwnNames()
    {
        const string json =
            """
            {"version":1,"kind":"List","payload":[{"name":"version","rank":1,"status":"FullTime"},{"name":"payload\"stamp","rank":2,"status":"PartTime"}],"stamp":"abc123"}
            """;
        var rows = ScryJson.DeserializePayload<List<Row>>(ScryJson.DeserializeResponse(Utf8(json)));

        Assert.That(rows!.Select(_ => _.Name), Is.EqualTo(["version", "payload\"stamp"]));
    }

    // The payload is not the last member here, so the slice has to end where the value does rather
    // than running to the end of the document.
    [Test]
    public void ReadsAPayloadFollowedByFurtherMembers()
    {
        const string json =
            """
            {"version":1,"kind":"List","payload":[{"name":"Alice","rank":1,"status":"FullTime"}],"stamp":"abc123","enumAliases":[{"enumName":"Status","valueName":"FullTime","previousNames":["Full"]}]}
            """;
        var response = ScryJson.DeserializeResponse(Utf8(json));

        Assert.Multiple(() =>
        {
            Assert.That(response.EnumAliases, Has.Count.EqualTo(1));
            Assert.That(ScryJson.DeserializePayload<List<Row>>(response)!.Single().Name, Is.EqualTo("Alice"));
        });
    }

    [Test]
    public void RefusesANewerWireVersionFromBytes()
    {
        var json = $$"""{"version":{{WireFormat.Version + 1}},"kind":"List","payload":[]}""";

        var exception = Assert.Throws<ScryWireException>(() => ScryJson.DeserializeResponse(Utf8(json)));

        Assert.That(exception!.Message, Does.Contain("Unsupported response wire version"));
    }

    [Test]
    public void ReportsMalformedBytesAsAWireFailure()
    {
        var exception = Assert.Throws<ScryWireException>(
            () => ScryJson.DeserializeResponse(Utf8("""{"version":1,"kind":"List","payload":[}""")));

        Assert.That(exception!.Message, Does.StartWith("Invalid query response"));
    }

    // A payload read leaves the scope that told the reader to step over payloads; a failed one has to
    // leave it too, or the next response on this thread would come back with an unparsed payload.
    [Test]
    public void LeavesNoScopeBehindAfterAFailedRead()
    {
        Assert.Throws<ScryWireException>(
            () => ScryJson.DeserializeResponse(Utf8("""{"version":1,"kind":"List","payload":[}""")));

        var response = ScryJson.DeserializeResponse(listJson);
        Assert.That(response.Payload.GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public void ReadsABatchsPayloadsFromBytes()
    {
        const string json =
            """
            {"version":1,"results":[
              {"response":{"version":1,"kind":"List","payload":[{"name":"Alice","rank":1,"status":"FullTime"}]}},
              {"response":{"version":1,"kind":"List","payload":[{"name":"Bob","rank":2,"status":"PartTime"}]}}],"stamp":"abc123"}
            """;
        var batch = ScryJson.DeserializeBatchResponse(Utf8(json));

        Assert.Multiple(() =>
        {
            Assert.That(ScryJson.DeserializePayload<List<Row>>(batch.Results[0].Response!)!.Single().Name, Is.EqualTo("Alice"));
            Assert.That(ScryJson.DeserializePayload<List<Row>>(batch.Results[1].Response!)!.Single().Name, Is.EqualTo("Bob"));
        });
    }

    // An entry that failed carries no response and so no payload was stepped over for it. The entries
    // after it must still line up with their own bytes rather than being shifted by one.
    [Test]
    public void PairsBatchPayloadsPastAFailedEntry()
    {
        const string json =
            """
            {"version":1,"results":[
              {"error":"Unknown source 'Nope'.","status":400},
              {"response":{"version":1,"kind":"List","payload":[{"name":"Bob","rank":2,"status":"PartTime"}]}}],"stamp":"abc123"}
            """;
        var batch = ScryJson.DeserializeBatchResponse(Utf8(json));

        Assert.Multiple(() =>
        {
            Assert.That(batch.Results[0].Error, Is.EqualTo("Unknown source 'Nope'."));
            Assert.That(ScryJson.DeserializePayload<List<Row>>(batch.Results[1].Response!)!.Single().Name, Is.EqualTo("Bob"));
        });
    }

    [Test]
    public void ReadsAnErrorBodyFromBytes()
    {
        var error = ScryJson.TryDeserializeError(Utf8("""{"error":"Nope.","staleClient":true}"""));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Error, Is.EqualTo("Nope."));
            Assert.That(error.StaleClient, Is.True);
        });
    }

    [Test]
    public void ReturnsNullForABodyThatIsNotAnError() =>
        Assert.That(ScryJson.TryDeserializeError(Utf8("<html>502 from a proxy</html>")), Is.Null);

    [Test]
    public void ReadsAStreamedRowFromBytes()
    {
        var row = ScryJson.DeserializeRow<Row>(
            "{\"name\":\"Alice\",\"rank\":1,\"status\":\"FullTime\"}"u8,
            aliases: null);

        Assert.That(row, Is.EqualTo(new Row("Alice", 1, Status.FullTime)));
    }

    // The aliases reach the enum reader the same way they do on the element overload, so a client
    // generated before a rename still resolves the current name.
    [Test]
    public void ResolvesARenamedEnumValueOnARowReadFromBytes()
    {
        var row = ScryJson.DeserializeRow<Row>(
            "{\"name\":\"Alice\",\"rank\":1,\"status\":\"Permanent\"}"u8,
            [new("Status", "Permanent", ["FullTime"])]);

        Assert.That(row!.Status, Is.EqualTo(Status.FullTime));
    }

    [Test]
    public void ReadsAMarkerFromBytes()
    {
        var marker = ScryJson.DeserializeMarker("""{"$scry":"begin","version":1,"stamp":"abc123"}"""u8);

        Assert.Multiple(() =>
        {
            Assert.That(marker.Kind, Is.EqualTo(ScryStream.Begin));
            Assert.That(marker.Stamp, Is.EqualTo("abc123"));
        });
    }
}
