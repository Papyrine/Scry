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
/// The single-response shape only: the explorer neither batches nor streams, and a stream numbers its
/// parts per row rather than per document.
/// </para>
/// </remarks>
public static class BinaryResponseReader
{
    /// <summary>
    /// The response body as JSON. A plain response is returned as it arrived; a multipart one is
    /// reassembled. Error responses are never multipart, so a failure reads as its own body either way.
    /// </summary>
    public static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancel = default)
    {
        if (!MultipartResponse.TryGetBoundary(response, out var boundary))
        {
            return await response.Content.ReadAsStringAsync(cancel);
        }

        var (envelope, parts) = await MultipartResponse.ReadAsync(response, boundary, cancel);
        // The explorer's product is JSON text either way, so this is the one caller that does want the
        // envelope as a string — the typed client keeps the bytes and parses them into its own model.
        return Inline(Encoding.UTF8.GetString(envelope.Span), parts);
    }

    /// <summary>
    /// Replaces every <c>{"$bin":n}</c> placeholder in the envelope with the base64 of the part it
    /// names, leaving the rest of the document byte-identical.
    /// </summary>
    public static string Inline(string envelope, IReadOnlyList<byte[]> parts)
    {
        using var document = JsonDocument.Parse(envelope);
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            Write(json, document.RootElement, parts);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void Write(Utf8JsonWriter json, JsonElement element, IReadOnlyList<byte[]> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryPart(element, parts, out var bytes))
                {
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
