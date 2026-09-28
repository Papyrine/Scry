public class BinaryConverterTests
{
    record Row(string Name, byte[]? Avatar);

    // The built-in byte[] handling, for proving the shared options do not diverge from it.
    static JsonSerializerOptions builtIn = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Test]
    public async Task WritesBase64IdenticalToBuiltIn()
    {
        var row = new Row("Alice", [0x01, 0x02, 0x03]);
        var viaOptions = JsonSerializer.Serialize(row, ScryJson.Options);
        var viaBuiltIn = JsonSerializer.Serialize(row, builtIn);
        await Assert.That(viaOptions).IsEqualTo(viaBuiltIn);
        await Assert.That(viaOptions).Contains("\"AQID\"");
    }

    [Test]
    public async Task ReadsBase64WithoutAScope()
    {
        var row = JsonSerializer.Deserialize<Row>("""{"name":"Alice","avatar":"AQID"}""", ScryJson.Options);
        await Assert.That(row!.Avatar).IsEquivalentTo(new byte[] {0x01, 0x02, 0x03}, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResolvesAPlaceholderAgainstTheResponseParts()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":0}}]""") with
        {
            BinaryParts = [[0x01, 0x02, 0x03]]
        };
        var rows = ScryJson.DeserializePayload<List<Row>>(response);
        await Assert.That(rows![0].Avatar).IsEquivalentTo(new byte[] {0x01, 0x02, 0x03}, CollectionOrdering.Matching);
    }

    [Test]
    public async Task NullStaysInlineBesidePlaceholders()
    {
        var response = Response("""[{"name":"Alice","avatar":null},{"name":"Bob","avatar":{"$bin":0}}]""") with
        {
            BinaryParts = [[0x0A]]
        };
        var rows = ScryJson.DeserializePayload<List<Row>>(response);
        await Assert.That(rows![0].Avatar).IsNull();
        await Assert.That(rows[1].Avatar).IsEquivalentTo(new byte[] {0x0A}, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PlaceholderWithoutPartsFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":0}}]""");
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("outside a response carrying binary parts");
    }

    [Test]
    public async Task PlaceholderIndexOutOfRangeFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":1}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("references part 1");
    }

    [Test]
    public void MalformedPlaceholderFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"other":0}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
    }

    // Everything that is neither a base64 string nor an object cannot be a byte[] at all.
    [Test]
    [Arguments("0")]
    [Arguments("true")]
    [Arguments("[1,2,3]")]
    public async Task NonBinaryTokenFailsClosed(string avatar)
    {
        var response = Response($$"""[{"name":"Alice","avatar":{{avatar}}}]""");
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("Expected a base64 string");
    }

    [Test]
    public async Task EmptyPlaceholderFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{}}]""");
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("Expected a single $bin property");
    }

    // The index is read as a number, so a part cannot be named by a string that merely looks like one.
    [Test]
    public async Task NonNumericPartIndexFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":"0"}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("Expected a part index");
    }

    // A number that is not an Int32 is not an index either — and it fails as a wire fault rather than
    // as whatever the reader would have raised on its own.
    [Test]
    [Arguments("1.5")]
    [Arguments("99999999999")]
    public async Task NonIntegerPartIndexFailsClosed(string index)
    {
        var response = Response($$$"""[{"name":"Alice","avatar":{"$bin":{{{index}}}}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("Expected a part index");
    }

    [Test]
    public async Task NegativePartIndexFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":-1}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("references part -1");
    }

    // A placeholder names one part and nothing else: a second property would be a shape the writer
    // never emits, and reading past it would leave the reader mid-object.
    [Test]
    public async Task PlaceholderWithExtraPropertiesFailsClosed()
    {
        var response = Response("""[{"name":"Alice","avatar":{"$bin":0,"other":1}}]""") with
        {
            BinaryParts = [[0x01]]
        };
        var exception = Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(response));
        await Assert.That(exception!.Message).Contains("carry only");
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
        Assert.ThrowsExactly<JsonException>(() => ScryJson.DeserializePayload<List<Row>>(bare));
    }

    static QueryResponse Response(string payload) =>
        QueryResponse.Create(ResultKind.List, JsonSerializer.Deserialize<JsonElement>(payload));
}
