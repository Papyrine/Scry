/// <summary>
/// Writes the multipart framing a binary-carrying response travels in: parts opened with a boundary
/// line and headers, content written raw by the caller, the whole body closed by a terminator. The
/// delimiter's leading CRLF is written by the <b>next</b> part (or the terminator), which is what
/// keeps every part's content byte-exact — a reader strips that CRLF as part of the delimiter.
/// </summary>
sealed class MultipartWriter(Stream body, string boundary)
{
    public string Boundary { get; } = boundary;

    public string ContentType { get; } = $"{ScryBinary.ContentType}; boundary={boundary}";

    bool first = true;

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
        await Open($"Content-Type: {ScryBinary.PartContentType}\r\nContent-Length: {bytes.Length}", cancel);
        await body.WriteAsync(bytes, cancel);
    }

    /// <summary>Opens a part of <paramref name="contentType"/>; the caller writes the content raw.</summary>
    public Task OpenPart(string contentType, Cancel cancel) =>
        Open($"Content-Type: {contentType}", cancel);

    /// <summary>Closes the body. Nothing may be written after this.</summary>
    public Task Terminate(Cancel cancel) =>
        Write($"\r\n--{Boundary}--\r\n", cancel);

    Task Open(string headers, Cancel cancel)
    {
        var delimiter = first ? $"--{Boundary}\r\n" : $"\r\n--{Boundary}\r\n";
        first = false;
        return Write($"{delimiter}{headers}\r\n\r\n", cancel);
    }

    Task Write(string text, Cancel cancel) =>
        body.WriteAsync(Encoding.ASCII.GetBytes(text), cancel).AsTask();
}
