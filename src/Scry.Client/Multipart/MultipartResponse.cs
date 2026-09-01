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

        // The media type says multipart, so a missing boundary is a malformed response rather than a
        // response of some other shape — which is why this throws where the extension returns false.
        if (!response.Content.TryGetMultipartBoundary(out boundary))
        {
            throw new ScryWireException("A multipart response arrived without a boundary.");
        }

        return true;
    }

    /// <summary>
    /// Reads a single/batch multipart body: binary parts in wire order, then the JSON envelope as the
    /// final section. Sections must be consumed in order — the reader is forward-only.
    /// </summary>
    /// <remarks>
    /// The envelope comes back as the UTF-8 it arrived as, for the reason the plain path reads bytes:
    /// the JSON reader wants them, and the response keeps them so its payload is parsed once.
    /// </remarks>
    public static async Task<(ReadOnlyMemory<byte> Envelope, IReadOnlyList<byte[]> Parts)> ReadAsync(
        HttpResponseMessage response,
        string boundary,
        Cancel cancel)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        using var reader = new MultipartReader(boundary, body);
        var parts = new List<byte[]>();
        byte[]? envelope = null;
        while (await reader.ReadNextSectionAsync(cancel) is { } section)
        {
            if (IsBinary(section))
            {
                parts.Add(await ReadPartBytes(section, cancel));
                continue;
            }

            envelope = await ReadPartBytes(section, cancel);
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

    public static Task<byte[]> ReadPartBytes(MultipartSection section, Cancel cancel) =>
        section.ReadAsBytesAsync(cancel);
}
