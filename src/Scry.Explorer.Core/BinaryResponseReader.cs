namespace Scry;

/// <summary>
/// Reads a query response into the JSON text the explorer displays and shapes its result table from.
/// A response carrying <c>[BinaryTransfer]</c> values arrives as <see cref="ScryBinary.ContentType"/>
/// — raw parts, then a JSON envelope leaving <c>{"$bin":n}</c> where each diverted value was — and is
/// read back into a single JSON document with those values inlined as base64.
/// </summary>
/// <remarks>
/// Inlining rather than surfacing the placeholder is what keeps the explorer honest about the
/// attribute: <c>[BinaryTransfer]</c> is a transfer encoding and nothing else, so a member carrying it
/// must render, export, and tabulate exactly as the same <c>byte[]</c> would without it. Base64 is the
/// form the value would have arrived in — the generated client resolves the placeholder to the same
/// bytes through <c>ScryJson.DeserializePayload</c>, which the explorer cannot use because its rows
/// are <see cref="JsonElement"/>s rather than a projected type.
/// <para>
/// Up to <see cref="InlineLimit"/>, that is. A larger part is replaced by a short description of
/// itself: inlined, its base64 would be copied into the parsed document, the indented response text,
/// its highlighting, and a table cell — several times its size in a WASM heap, for a value nobody can
/// read in a 12rem cell or a wall of JSON.
/// </para>
/// <para>
/// The single-response shape only: the explorer neither batches nor streams, and a stream numbers its
/// parts per row rather than per document.
/// </para>
/// </remarks>
public static class BinaryResponseReader
{
    /// <summary>The largest part, in bytes, inlined as base64; a larger one is described instead.</summary>
    public const int InlineLimit = 64 * 1024;

    /// <summary>
    /// The response body as UTF-8 JSON. A plain response is returned as it arrived; a multipart one is
    /// reassembled. Error responses are never multipart, so a failure reads as its own body either way.
    /// </summary>
    /// <remarks>
    /// Bytes rather than text: the caller both parses the body and prettifies it for display, and each
    /// reads UTF-8 directly — so the only string made is the displayed one.
    /// </remarks>
    public static async Task<ReadOnlyMemory<byte>> ReadAsync(HttpResponseMessage response, Cancel cancel = default)
    {
        if (!MultipartResponse.TryGetBoundary(response, out var boundary))
        {
            return await response.Content.ReadAsByteArrayAsync(cancel);
        }

        var (envelope, parts) = await MultipartResponse.ReadAsync(response, boundary, cancel);
        return Inline(envelope, parts);
    }

    /// <summary>
    /// Replaces every <c>{"$bin":n}</c> placeholder in the envelope with the base64 of the part it
    /// names — or, past <see cref="InlineLimit"/>, a string giving its size — leaving the rest of the
    /// document byte-identical.
    /// </summary>
    public static ReadOnlyMemory<byte> Inline(ReadOnlyMemory<byte> envelope, IReadOnlyList<byte[]> parts)
    {
        using var document = JsonDocument.Parse(envelope);
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            Write(json, document.RootElement, parts);
        }

        return buffer.WrittenMemory;
    }

    static void Write(Utf8JsonWriter json, JsonElement element, IReadOnlyList<byte[]> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryPart(element, parts, out var bytes))
                {
                    if (bytes.Length > InlineLimit)
                    {
                        json.WriteStringValue(Describe(bytes.Length));
                        return;
                    }

                    json.WriteBase64StringValue(bytes);
                    return;
                }

                json.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    json.WritePropertyName(property.Name);
                    Write(json, property.Value, parts);
                }

                json.WriteEndObject();
                return;

            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(json, item, parts);
                }

                json.WriteEndArray();
                return;

            default:
                element.WriteTo(json);
                return;
        }
    }

    /// <summary>
    /// What a part too large to inline is shown as. Bracketed so it cannot be read as base64, which
    /// has no '[' in its alphabet.
    /// </summary>
    public static string Describe(int length) =>
        string.Create(CultureInfo.InvariantCulture, $"[binary: {length:N0} bytes]");

    /// <summary>
    /// The part an object names, if it is a placeholder. A projected member name comes from the
    /// caller's own C# identifiers and cannot start with '$', so an object carrying that property is a
    /// placeholder and nothing else — which is why a malformed one fails the read rather than being
    /// rendered as an object. The same checks <c>BinaryConverter</c> applies on the typed path.
    /// </summary>
    static bool TryPart(JsonElement element, IReadOnlyList<byte[]> parts, out byte[] bytes)
    {
        bytes = [];
        if (!element.TryGetProperty(ScryBinary.PartProperty, out var index))
        {
            return false;
        }

        if (element.EnumerateObject().Count() != 1)
        {
            throw new ScryWireException(
                $"Expected a binary placeholder to carry only {ScryBinary.PartProperty}.");
        }

        // Read as an Int32 rather than asked for one: a number the index cannot be — fractional, or
        // past int range — is as malformed as a string would be, and says so the same way.
        if (index.ValueKind != JsonValueKind.Number ||
            !index.TryGetInt32(out var position))
        {
            throw new ScryWireException(
                $"Expected a part index as the value of {ScryBinary.PartProperty}.");
        }

        if (position < 0 ||
            position >= parts.Count)
        {
            throw new ScryWireException(
                $"A {ScryBinary.PartProperty} placeholder references part {position}, but the response carried {parts.Count} parts.");
        }

        bytes = parts[position];
        return true;
    }
}
