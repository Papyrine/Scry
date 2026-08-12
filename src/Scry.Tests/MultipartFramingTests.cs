/// <summary>
/// The multipart framing a binary-carrying response travels in. The writer encodes the boundary once
/// and caches the opening bytes of a repeated part type — a stream opens one part per row — so what
/// this pins is that the cached opening is the same framing an uncached one produces, and that the
/// leading CRLF still belongs to the delimiter rather than to the part before it.
/// </summary>
[TestFixture]
public class MultipartFramingTests
{
    [Test]
    public async Task FramesRepeatedPartsIdentically()
    {
        using var body = new MemoryStream();
        var writer = MultipartWriter.Create(body);

        await writer.OpenPart("application/x-ndjson", default);
        await body.WriteAsync("row1"u8.ToArray());
        await writer.OpenPart("application/x-ndjson", default);
        await body.WriteAsync("row2"u8.ToArray());
        // The third goes through the cached opening; the second is what filled it.
        await writer.OpenPart("application/x-ndjson", default);
        await body.WriteAsync("row3"u8.ToArray());
        await writer.Terminate(default);

        var expected =
            $"--{writer.Boundary}\r\nContent-Type: application/x-ndjson\r\n\r\nrow1" +
            $"\r\n--{writer.Boundary}\r\nContent-Type: application/x-ndjson\r\n\r\nrow2" +
            $"\r\n--{writer.Boundary}\r\nContent-Type: application/x-ndjson\r\n\r\nrow3" +
            $"\r\n--{writer.Boundary}--\r\n";

        Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo(expected));
    }

    [Test]
    public async Task FramesABinaryPartWithItsLength()
    {
        using var body = new MemoryStream();
        var writer = MultipartWriter.Create(body);

        await writer.WriteBinary([1, 2, 3], default);
        await writer.OpenPart("application/json", default);
        await body.WriteAsync("""{"ok":true}"""u8.ToArray());
        await writer.Terminate(default);

        var expected =
            $"--{writer.Boundary}\r\nContent-Type: {ScryBinary.PartContentType}\r\nContent-Length: 3\r\n\r\n" +
            $"\r\n--{writer.Boundary}\r\nContent-Type: application/json\r\n\r\n{{\"ok\":true}}" +
            $"\r\n--{writer.Boundary}--\r\n";

        Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo(expected));
    }

    // Alternating types is the batch shape, and it must not serve one type's cached opening for the
    // other.
    [Test]
    public async Task DoesNotReuseAnOpeningAcrossContentTypes()
    {
        using var body = new MemoryStream();
        var writer = MultipartWriter.Create(body);

        await writer.OpenPart("application/x-ndjson", default);
        await writer.OpenPart("application/json", default);
        await writer.OpenPart("application/x-ndjson", default);
        await writer.Terminate(default);

        var expected =
            $"--{writer.Boundary}\r\nContent-Type: application/x-ndjson\r\n\r\n" +
            $"\r\n--{writer.Boundary}\r\nContent-Type: application/json\r\n\r\n" +
            $"\r\n--{writer.Boundary}\r\nContent-Type: application/x-ndjson\r\n\r\n" +
            $"\r\n--{writer.Boundary}--\r\n";

        Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo(expected));
    }

    [Test]
    public void AdvertisesTheBoundaryOnTheContentType()
    {
        using var body = new MemoryStream();
        var writer = MultipartWriter.Create(body);

        Assert.That(writer.ContentType, Is.EqualTo($"{ScryBinary.ContentType}; boundary={writer.Boundary}"));
    }
}
