/// <summary>
/// Writes the multipart framing a binary-carrying response travels in: parts opened with a boundary
/// line and headers, content written raw by the caller, the whole body closed by a terminator. The
/// delimiter's leading CRLF is written by the <b>next</b> part (or the terminator), which is what
/// keeps every part's content byte-exact — a reader strips that CRLF as part of the delimiter.
/// </summary>
/// <remarks>
/// The boundary is fixed for the response, so every framing byte that does not depend on the part is
/// encoded once here rather than interpolated and re-encoded per part. The streaming path opens a
/// part per row, always with the same content type, so the opening bytes for the last one opened are
/// kept and reused — which makes the per-row framing cost a single write of a cached array.
/// </remarks>
sealed class MultipartWriter
{
    readonly Stream body;
    readonly byte[] firstDelimiter;
    readonly byte[] delimiter;
    readonly byte[] terminator;

    // The last content type OpenPart was called with, and the complete opening bytes for it — the
    // delimiter and the headers together, since after the first part those never differ again.
    string? openedType;
    byte[]? opening;

    bool first = true;

    MultipartWriter(Stream body, string boundary)
    {
        this.body = body;
        Boundary = boundary;
        ContentType = $"{ScryBinary.ContentType}; boundary={boundary}";
        firstDelimiter = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
        delimiter = Encoding.ASCII.GetBytes($"\r\n--{boundary}\r\n");
        terminator = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
    }

    public string Boundary { get; }

    public string ContentType { get; }

    /// <summary>
    /// A fresh boundary per response. The content is never scanned for collisions: 122 bits of
    /// randomness make an accidental delimiter sequence in part content cryptographically negligible,
    /// the same bet the BCL's own multipart writers make.
    /// </summary>
    public static MultipartWriter Create(Stream body) =>
        new(body, ScryBinary.BoundaryPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>Opens a raw binary part and writes its bytes.</summary>
    public async Task WriteBinary(byte[] bytes, Cancel cancel)
    {
        // Content-Length differs per part, so this one header is built each time. A binary part is
        // large by nature, which is what makes that not worth avoiding.
        await Open(
            Encoding.ASCII.GetBytes($"Content-Type: {ScryBinary.PartContentType}\r\nContent-Length: {bytes.Length}\r\n\r\n"),
            cancel);
        await body.WriteAsync(bytes, cancel);
    }

    /// <summary>Opens a part of <paramref name="contentType"/>; the caller writes the content raw.</summary>
    public async Task OpenPart(string contentType, Cancel cancel)
    {
        // Cached past the first part, where the delimiter stops differing — so a stream opening a part
        // per row writes one array it built once.
        if (!first &&
            openedType == contentType &&
            opening is { } cached)
        {
            await body.WriteAsync(cached, cancel);
            return;
        }

        var headers = Encoding.ASCII.GetBytes($"Content-Type: {contentType}\r\n\r\n");
        var wasFirst = first;
        await Open(headers, cancel);
        if (!wasFirst)
        {
            openedType = contentType;
            opening = [.. delimiter, .. headers];
        }
    }

    /// <summary>Closes the body. Nothing may be written after this.</summary>
    public Task Terminate(Cancel cancel) =>
        body.WriteAsync(terminator, cancel).AsTask();

    async Task Open(byte[] headers, Cancel cancel)
    {
        await body.WriteAsync(first ? firstDelimiter : delimiter, cancel);
        first = false;
        await body.WriteAsync(headers, cancel);
    }
}
