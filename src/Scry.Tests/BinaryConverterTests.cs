[TestFixture]
public class BinaryConverterTests
{
    record Row(string Name, byte[]? Avatar);

    // The built-in byte[] handling, for proving the shared options do not diverge from it.
    static readonly JsonSerializerOptions builtIn = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Test]
    public void WritesBase64IdenticalToBuiltIn()
    {
        var row = new Row("Alice", [0x01, 0x02, 0x03]);
        var viaOptions = JsonSerializer.Serialize(row, ScryJson.Options);
        var viaBuiltIn = JsonSerializer.Serialize(row, builtIn);
        Assert.That(viaOptions, Is.EqualTo(viaBuiltIn));
        Assert.That(viaOptions, Does.Contain("\"AQID\""));
    }

    [Test]
    public void ReadsBase64WithoutAScope()
    {
        var row = JsonSerializer.Deserialize<Row>("""{"name":"Alice","avatar":"AQID"}""", ScryJson.Options);
        Assert.That(row!.Avatar, Is.EqualTo(new byte[] {0x01, 0x02, 0x03}));
    }

    [Test]
    public void ResolvesAPlaceholderAgainstTheResponseParts()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":0}}]""") with
        {
            BinaryParts = [[0x01, 0x02, 0x03]]
        };
        var rows = ScryJson.DeserializePayload<List<Row>>(response);
        Assert.That(rows![0].Avatar, Is.EqualTo(new byte[] {0x01, 0x02, 0x03}));
    }

    [Test]
    public void NullStaysInlineBesidePlaceholders()
    {
        var response = Response("""[{"name":"Alice","avatar":null},{"name":"Bob","avatar":{"$bin":0}}]""") with
        {
            BinaryParts = [[0x0A]]
        };
        var rows = ScryJson.DeserializePayload<List<Row>>(response);
        Assert.That(rows![0].Avatar, Is.Null);
        Assert.That(rows[1].Avatar, Is.EqualTo(new byte[] {0x0A}));
    }

    [Test]
    public void PlaceholderWithoutPartsFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":0}}]""");
        var exception = Assert.Throws<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        Assert.That(exception!.Message, Does.Contain("outside a response carrying binary parts"));
    }

    [Test]
    public void PlaceholderIndexOutOfRangeFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":1}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        var exception = Assert.Throws<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        Assert.That(exception!.Message, Does.Contain("references part 1"));
    }

    [Test]
    public void MalformedPlaceholderFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"other":0}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        Assert.Throws<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
    }

    [Test]
    public void ScopeDoesNotLeakAcrossDeserializations()
    {
        var carried = Response("""[{"name":"Alice","avatar":{"$bin":0}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        ScryJson.DeserializePayload<List<Row>>(carried);

        var bare = Response("""[{"name":"Alice","avatar":{"$bin":0}}]""");
        Assert.Throws<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(bare));
    }

    static QueryResponse Response(string payload) =>
        QueryResponse.Create(ResultKind.List, JsonSerializer.Deserialize<JsonElement>(payload));
}
