/// <summary>
/// Reads the multipart response a result carrying <c>[BinaryTransfer]</c> values arrives in: raw
/// binary parts in wire order, then the JSON document that references them. Shared by the client's
/// own transports and by the in-tree explorer, so the format has a single reader.
/// </summary>
static class MultipartResponse
{
    /// <summary>
    /// Whether the response is the multipart format a binary-carrying result travels in, and its
    /// boundary if so. The reader strips a quoted boundary itself, so the raw parameter is passed on.
    /// </summary>
    public static bool TryGetBoundary(HttpResponseMessage response, [NotNullWhen(true)] out string? boundary)
    {
        boundary = null;
        var contentType = response.Content.Headers.ContentType;
        if (!string.Equals(contentType?.MediaType, ScryBinary.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        boundary = contentType!.Parameters
            .FirstOrDefault(_ => string.Equals(_.Name, "boundary", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrEmpty(boundary))
        {
            throw new ScryWireException("A multipart response arrived without a boundary.");
        }

        return true;
    }

    /// <summary>
    /// Reads a single/batch multipart body: binary parts in wire order, then the JSON envelope as the
    /// final section. Sections must be consumed in order — the reader is forward-only.
    /// </summary>
    public static async Task<(string Envelope, IReadOnlyList<byte[]> Parts)> ReadAsync(
        HttpResponseMessage response,
        string boundary,
        Cancel cancel)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        var reader = new MultipartReader(boundary, body);
        var parts = new List<byte[]>();
        string? envelope = null;
        while (await reader.ReadNextSectionAsync(cancel) is { } section)
        {
            if (IsBinary(section))
            {
                parts.Add(await ReadPartBytes(section, cancel));
                continue;
            }

            using var text = new StreamReader(section.Body);
            envelope = await text.ReadToEndAsync(cancel);
        }

        if (envelope is null)
        {
            throw new ScryWireException("A multipart response arrived without a JSON part.");
        }

        return (envelope, parts);
    }

    /// <summary>Whether the section carries a raw binary part rather than the JSON that references it.</summary>
    public static bool IsBinary(MultipartSection section) =>
        string.Equals(section.ContentType, ScryBinary.PartContentType, StringComparison.OrdinalIgnoreCase);

    public static async Task<byte[]> ReadPartBytes(MultipartSection section, Cancel cancel)
    {
        // Content-Length is advisory — used to size the buffer, never trusted for the read itself.
        using var memory = section.ContentLength is { } length
            ? new MemoryStream(length)
            : new MemoryStream();
        await section.Body.CopyToAsync(memory, cancel);
        return memory.ToArray();
    }
}
