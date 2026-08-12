namespace Scry;

public sealed partial record QueryResponse
{
    /// <summary>
    /// Where a parsed payload is kept. A cell rather than a field on the record itself, because the
    /// record's equality and hash code are over its fields: parsing on first read would otherwise
    /// change both, and a value whose hash changes when you look at one of its members is not one that
    /// can be put in a dictionary. The cell's reference is fixed at construction, so what equality sees
    /// never moves — only what the cell holds does.
    /// </summary>
    sealed class PayloadCell(JsonElement? parsed)
    {
        public JsonElement? Parsed = parsed;
    }

    // Initialized from the positional parameter, which is what constructing a response with a payload
    // assigns: the property below replaces the one the record would have synthesized, so the
    // constructor has this to write through instead. A response the server built, or one read from a
    // string body, arrives with the payload already in the cell and behaves exactly as it always has.
    PayloadCell cell = new(Payload.ValueKind == JsonValueKind.Undefined ? null : Payload);

    /// <summary>
    /// The result, as JSON. See the remarks on <see cref="QueryResponse"/> for its shape per
    /// <see cref="ResultKind"/>.
    /// </summary>
    /// <remarks>
    /// Parsed on first read when the response came from <c>ScryJson.DeserializeResponse</c> over bytes
    /// the caller still holds. A list or page result is never read through here — the client asks
    /// <c>ScryJson.DeserializePayload</c> for its own type instead, which reads
    /// <see cref="RawPayload"/> — so the document this would otherwise build is one nothing would look
    /// at. A racing double parse produces equivalent documents and one of them wins, which is harmless.
    /// </remarks>
    // Ordered explicitly for the reason given on QueryResponse: declaring the property here rather
    // than letting the record synthesize it would otherwise move it to the end of the JSON.
    [JsonPropertyOrder(2)]
    [JsonConverter(typeof(PayloadConverter))]
    public JsonElement Payload
    {
        get
        {
            if (cell.Parsed is { } already)
            {
                return already;
            }

            if (RawPayload.IsEmpty)
            {
                return default;
            }

            var parsed = JsonDocument.Parse(RawPayload).RootElement;
            cell.Parsed = parsed;
            return parsed;
        }
        // A fresh cell rather than a write into the current one: a `with` copy carries the same cell as
        // the response it was copied from, and replacing that copy's payload must not reach back and
        // change the original's. Undefined is what the converter returns for a payload it stepped over,
        // and what a default JsonElement is; neither is a parsed payload, so neither is recorded as one.
        init => cell = new(value.ValueKind == JsonValueKind.Undefined ? null : value);
    }

    /// <summary>
    /// The payload exactly as it arrived, when the response was read from UTF-8 the caller still
    /// holds. <c>ScryJson.DeserializePayload</c> reads the result out of these bytes directly, which
    /// is one parse — where going through <see cref="Payload"/> is a parse into a document, a write of
    /// that document back out to a buffer, and a parse of the buffer.
    /// </summary>
    /// <remarks>
    /// Empty for every response built any other way, which is what leaves those on the
    /// <see cref="Payload"/> path unchanged. Never serialized: the bytes are the payload, not a member
    /// beside it.
    /// </remarks>
    [JsonIgnore]
    internal ReadOnlyMemory<byte> RawPayload { get; init; }
}
