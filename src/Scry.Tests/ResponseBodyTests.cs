/// <summary>
/// A response body read into one array sized by its declared length. The length sizes the array and
/// is never trusted for the read, in either direction: a body short of it is refused, and one longer
/// than it — decoded by a handler that left the encoded length in place — is read to its end.
/// </summary>
public class ResponseBodyTests
{
    [Test]
    public async Task ABodyLongerThanItsLengthIsReadWhole()
    {
        var body = "{\"kind\":\"List\",\"payload\":[1,2,3]}"u8.ToArray();
        using var content = new StreamContent(new MemoryStream(body));
        content.Headers.ContentLength = 12;

        var read = await ResponseBody.ReadAsync(content, Cancel.None);

        await Assert.That(read).IsEquivalentTo(body, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnEncodedBodyIsNotSizedByItsLength()
    {
        var body = "{\"kind\":\"Scalar\",\"payload\":3}"u8.ToArray();
        using var content = new StreamContent(new MemoryStream(body));
        content.Headers.ContentLength = 40;
        content.Headers.ContentEncoding.Add("gzip");

        var read = await ResponseBody.ReadAsync(content, Cancel.None);

        await Assert.That(read).IsEquivalentTo(body, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ABodyShortOfItsLengthIsRefused()
    {
        using var content = new StreamContent(new MemoryStream([1, 2, 3]));
        content.Headers.ContentLength = 10;

        var exception = await Assert.ThrowsAsync<ScryWireException>(() => ResponseBody.ReadAsync(content, Cancel.None));

        await Assert.That(exception!.Message).Contains("3 of the 10 bytes");
    }
}
