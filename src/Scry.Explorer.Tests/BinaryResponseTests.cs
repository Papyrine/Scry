// The explorer's side of [BinaryTransfer]: a result carrying diverted values arrives as multipart,
// and what the explorer shows has to be what the same query would have returned without the
// attribute — the placeholder folded back into the envelope as the base64 it stood in for.
public class BinaryResponseTests
{
    static string Envelope(string payload) =>
        $$"""{"version":2,"kind":"List","payload":{{payload}},"stamp":"abc"}""";

    [Test]
    public async Task InlinesAPlaceholderAsBase64()
    {
        var json = BinaryResponseReader.Inline(
            Envelope("""[{"name":"Alice","avatar":{"$bin":0}}]"""),
            [[0x01, 0x02, 0x03]]);

        await Assert.That(json).IsEqualTo(Envelope("""[{"name":"Alice","avatar":"AQID"}]"""));
    }

    // The bytes a non-diverted byte[] would have arrived as: identical to what BinaryConverter writes,
    // so a member reads the same whether or not the server diverted it.
    [Test]
    public async Task InlinedBytesMatchTheUndivertedEncoding()
    {
        byte[] bytes = [0xFB, 0xFF, 0x3E, 0x00];
        var json = BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":0}}]"""), [bytes]);

        await Assert.That(json).Contains(JsonSerializer.Serialize(bytes));
    }

    [Test]
    public async Task NullStaysInlineBesidePlaceholders()
    {
        var json = BinaryResponseReader.Inline(
            Envelope("""[{"avatar":null},{"avatar":{"$bin":0}}]"""),
            [[0x0A]]);

        await Assert.That(json).IsEqualTo(Envelope("""[{"avatar":null},{"avatar":"Cg=="}]"""));
    }

    // Parts are numbered across the whole document, and a projection into a navigation nests — so the
    // walk has to reach a placeholder at any depth, in any order.
    [Test]
    public async Task ResolvesPlaceholdersNestedAndOutOfOrder()
    {
        var json = BinaryResponseReader.Inline(
            Envelope("""[{"badge":{"$bin":1},"department":{"logo":{"$bin":0}}}]"""),
            [[0x01], [0x02]]);

        await Assert.That(json).IsEqualTo(Envelope("""[{"badge":"Ag==","department":{"logo":"AQ=="}}]"""));
    }

    // Nothing to resolve leaves the document as it arrived — the plain-JSON path costs the response
    // pane nothing but a reparse.
    [Test]
    public async Task LeavesADocumentWithoutPlaceholdersAlone()
    {
        var envelope = Envelope("""[{"name":"Alice","avatar":"AQID"}]""");

        await Assert.That(BinaryResponseReader.Inline(envelope, [])).IsEqualTo(envelope);
    }

    [Test]
    public async Task PlaceholderIndexOutOfRangeFailsClosed()
    {
        var exception = Assert.ThrowsExactly<ScryWireException>(
            () => BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":1}}]"""), [[0x01]]));

        await Assert.That(exception.Message).Contains("references part 1");
    }

    [Test]
    public async Task NegativePartIndexFailsClosed()
    {
        var exception = Assert.ThrowsExactly<ScryWireException>(
            () => BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":-1}}]"""), [[0x01]]));

        await Assert.That(exception.Message).Contains("references part -1");
    }

    // A part cannot be named by a string that merely looks like an index, nor by a number no index
    // can be.
    [Test]
    [Arguments(
        """
        "0"
        """)]
    [Arguments("1.5")]
    [Arguments("99999999999")]
    public async Task NonIntegerPartIndexFailsClosed(string index)
    {
        var exception = Assert.ThrowsExactly<ScryWireException>(() => BinaryResponseReader.Inline(Envelope($$$"""[{"avatar":{"$bin":{{{index}}}}}]"""), [[0x01]]));

        await Assert.That(exception.Message).Contains("Expected a part index");
    }

    [Test]
    public async Task PlaceholderWithExtraPropertiesFailsClosed()
    {
        var exception = Assert.ThrowsExactly<ScryWireException>(
            () => BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":0,"other":1}}]"""), [[0x01]]));

        await Assert.That(exception.Message).Contains("carry only");
    }

    // A member name comes from the caller's own C# identifiers, so a nested projection can never
    // collide with the placeholder property and is walked as the object it is.
    [Test]
    public async Task NestedProjectionIsNotMistakenForAPlaceholder()
    {
        var envelope = Envelope("""[{"department":{"name":"Engineering","id":1}}]""");

        await Assert.That(BinaryResponseReader.Inline(envelope, [[0x01]])).IsEqualTo(envelope);
    }

    [Test]
    public async Task ReadsAMultipartResponse()
    {
        var content = new MultipartContent("mixed", "scry-boundary");
        var part = new ByteArrayContent([0x01, 0x02, 0x03]);
        part.Headers.ContentType = new(ScryBinary.PartContentType);
        content.Add(part);
        content.Add(new StringContent(Envelope("""[{"avatar":{"$bin":0}}]"""), Encoding.UTF8, "application/json"));

        using var response = new HttpResponseMessage
        {
            Content = content
        };

        var json = await BinaryResponseReader.ReadAsync(response);

        await Assert.That(json).IsEqualTo(Envelope("""[{"avatar":"AQID"}]"""));
    }

    // The plain path: no multipart, so the body is whatever the server sent — including an error one,
    // which is never multipart.
    [Test]
    public async Task ReadsAPlainResponseAsItArrived()
    {
        var body = Envelope("""[{"name":"Alice"}]""");
        using var response = new HttpResponseMessage
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        await Assert.That(await BinaryResponseReader.ReadAsync(response)).IsEqualTo(body);
    }

    [Test]
    public async Task MultipartWithoutABoundaryFailsClosed()
    {
        using var response = new HttpResponseMessage
        {
            Content = new StringContent("", Encoding.UTF8, ScryBinary.ContentType)
        };

        var exception = await Assert.ThrowsExactlyAsync<ScryWireException>(() => BinaryResponseReader.ReadAsync(response));

        await Assert.That(exception!.Message).Contains("without a boundary");
    }

    [Test]
    public async Task MultipartWithoutAJsonPartFailsClosed()
    {
        var content = new MultipartContent("mixed", "scry-boundary");
        var part = new ByteArrayContent([0x01]);
        part.Headers.ContentType = new(ScryBinary.PartContentType);
        content.Add(part);

        using var response = new HttpResponseMessage
        {
            Content = content
        };

        var exception = await Assert.ThrowsExactlyAsync<ScryWireException>(() => BinaryResponseReader.ReadAsync(response));

        await Assert.That(exception!.Message).Contains("without a JSON part");
    }
}
