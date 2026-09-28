/// <summary>
/// What makes the multipart framing Scry's. The framing itself belongs to the HttpMultipart package
/// and is pinned by its own tests; all that is decided here is the media type the response is served
/// as, the boundary prefix, and the content type a raw binary part declares.
/// </summary>
public class ScryMultipartTests
{
    [Test]
    public async Task FramesABinaryPartAsScrysFormat()
    {
        using var body = new MemoryStream();
        var writer = ScryMultipart.Create(body);

        await writer.WriteBinary([1, 2, 3], Cancel.None);
        await writer.OpenPart("application/json", Cancel.None);
        await body.WriteAsync("""{"ok":true}"""u8.ToArray());
        await writer.Terminate(Cancel.None);

        // The three content bytes are named and interpolated rather than left in the expectation as
        // unreadable control characters.
        const string content = "\u0001\u0002\u0003";
        using (Assert.Multiple())
        {
            await Assert.That(writer.ContentType).IsEqualTo($"{ScryBinary.ContentType}; boundary={writer.Boundary}");
            await Assert.That(writer.Boundary).StartsWith(ScryBinary.BoundaryPrefix);
            await Assert.That(Encoding.ASCII.GetString(body.ToArray())).IsEqualTo(
                    $"--{writer.Boundary}\r\nContent-Type: {ScryBinary.PartContentType}\r\nContent-Length: 3\r\n\r\n{content}" +
                    $"\r\n--{writer.Boundary}\r\nContent-Type: application/json\r\n\r\n{{\"ok\":true}}" +
                    $"\r\n--{writer.Boundary}--\r\n");
        }
    }
}
