namespace Scry;

/// <summary>
/// The <see cref="MultipartWriter"/> defaults a binary-carrying response is written with: the media
/// type and boundary prefix of <see cref="ScryBinary"/>, and the content type each raw binary part
/// declares. The framing itself comes from the HttpMultipart package; this is only what makes it
/// Scry's format.
/// </summary>
static class ScryMultipart
{
    /// <summary>Opens a writer over the response body, boundary and content type already Scry's.</summary>
    public static MultipartWriter Create(Stream body) =>
        MultipartWriter.Create(body, ScryBinary.ContentType, ScryBinary.BoundaryPrefix);

    /// <summary>Writes one raw binary part, declaring its length.</summary>
    public static Task WriteBinary(this MultipartWriter writer, byte[] bytes, Cancel cancel) =>
        writer.WritePart(ScryBinary.PartContentType, bytes, cancel);
}
