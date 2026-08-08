// The explorer's side of [BinaryTransfer]: a result carrying diverted values arrives as multipart,
// and what the explorer shows has to be what the same query would have returned without the
// attribute — the placeholder folded back into the envelope as the base64 it stood in for.
[TestFixture]
public class BinaryResponseTests
{
    static string Envelope(string payload) =>
        $$"""{"version":2,"kind":"List","payload":{{payload}},"stamp":"abc"}""";

    [Test]
    public void InlinesAPlaceholderAsBase64()
    {
        var json = BinaryResponseReader.Inline(
            Envelope("""[{"name":"Alice","avatar":{"$bin":0}}]"""),
            [[0x01, 0x02, 0x03]]);

        Assert.That(json, Is.EqualTo(Envelope("""[{"name":"Alice","avatar":"AQID"}]""")));
    }

    // The bytes a non-diverted byte[] would have arrived as: identical to what BinaryConverter writes,
    // so a member reads the same whether or not the server diverted it.
    [Test]
    public void InlinedBytesMatchTheUndivertedEncoding()
    {
        byte[] bytes = [0xFB, 0xFF, 0x3E, 0x00];
        var json = BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":0}}]"""), [bytes]);

        Assert.That(json, Does.Contain(JsonSerializer.Serialize(bytes)));
    }

    [Test]
    public void NullStaysInlineBesidePlaceholders()
    {
        var json = BinaryResponseReader.Inline(
            Envelope("""[{"avatar":null},{"avatar":{"$bin":0}}]"""),
            [[0x0A]]);

        Assert.That(json, Is.EqualTo(Envelope("""[{"avatar":null},{"avatar":"Cg=="}]""")));
    }

    // Parts are numbered across the whole document, and a projection into a navigation nests — so the
    // walk has to reach a placeholder at any depth, in any order.
    [Test]
    public void ResolvesPlaceholdersNestedAndOutOfOrder()
    {
        var json = BinaryResponseReader.Inline(
            Envelope("""[{"badge":{"$bin":1},"department":{"logo":{"$bin":0}}}]"""),
            [[0x01], [0x02]]);

        Assert.That(json, Is.EqualTo(Envelope("""[{"badge":"Ag==","department":{"logo":"AQ=="}}]""")));
    }

    // Nothing to resolve leaves the document as it arrived — the plain-JSON path costs the response
    // pane nothing but a reparse.
    [Test]
    public void LeavesADocumentWithoutPlaceholdersAlone()
    {
        var envelope = Envelope("""[{"name":"Alice","avatar":"AQID"}]""");

        Assert.That(BinaryResponseReader.Inline(envelope, []), Is.EqualTo(envelope));
    }

    [Test]
    public void PlaceholderIndexOutOfRangeFailsClosed()
    {
        var exception = Assert.Throws<ScryWireException>(
            () => BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":1}}]"""), [[0x01]]));

        Assert.That(exception!.Message, Does.Contain("references part 1"));
    }

    [Test]
    public void NegativePartIndexFailsClosed()
    {
        var exception = Assert.Throws<ScryWireException>(
            () => BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":-1}}]"""), [[0x01]]));

        Assert.That(exception!.Message, Does.Contain("references part -1"));
    }

    // A part cannot be named by a string that merely looks like an index, nor by a number no index
    // can be.
    [TestCase(
        """
        "0"
        """)]
    [TestCase("1.5")]
    [TestCase("99999999999")]
    public void NonIntegerPartIndexFailsClosed(string index)
    {
        var exception = Assert.Throws<ScryWireException>(() => BinaryResponseReader.Inline(Envelope($$$"""[{"avatar":{"$bin":{{{index}}}}}]"""), [[0x01]]));

        Assert.That(exception!.Message, Does.Contain("Expected a part index"));
    }

    [Test]
    public void PlaceholderWithExtraPropertiesFailsClosed()
    {
        var exception = Assert.Throws<ScryWireException>(
            () => BinaryResponseReader.Inline(Envelope("""[{"avatar":{"$bin":0,"other":1}}]"""), [[0x01]]));

        Assert.That(exception!.Message, Does.Contain("carry only"));
    }

    // A member name comes from the caller's own C# identifiers, so a nested projection can never
    // collide with the placeholder property and is walked as the object it is.
    [Test]
    public void NestedProjectionIsNotMistakenForAPlaceholder()
    {
        var envelope = Envelope("""[{"department":{"name":"Engineering","id":1}}]""");

        Assert.That(BinaryResponseReader.Inline(envelope, [[0x01]]), Is.EqualTo(envelope));
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

        Assert.That(json, Is.EqualTo(Envelope("""[{"avatar":"AQID"}]""")));
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

        Assert.That(await BinaryResponseReader.ReadAsync(response), Is.EqualTo(body));
    }

    [Test]
    public void MultipartWithoutABoundaryFailsClosed()
    {
        using var response = new HttpResponseMessage
        {
            Content = new StringContent("", Encoding.UTF8, ScryBinary.ContentType)
        };

        var exception = Assert.ThrowsAsync<ScryWireException>(() => BinaryResponseReader.ReadAsync(response));

        Assert.That(exception!.Message, Does.Contain("without a boundary"));
    }

    [Test]
    public void MultipartWithoutAJsonPartFailsClosed()
    {
        var content = new MultipartContent("mixed", "scry-boundary");
        var part = new ByteArrayContent([0x01]);
        part.Headers.ContentType = new(ScryBinary.PartContentType);
        content.Add(part);

        using var response = new HttpResponseMessage
        {
            Content = content
        };

        var exception = Assert.ThrowsAsync<ScryWireException>(() => BinaryResponseReader.ReadAsync(response));

        Assert.That(exception!.Message, Does.Contain("without a JSON part"));
    }
}
