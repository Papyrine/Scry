/// <summary>
/// Reads and writes <see cref="QueryResponse.Payload"/>. Writing is what the serializer's own
/// <see cref="JsonElement"/> handling does, so a written response is byte-identical. Reading is too,
/// except inside a <see cref="PayloadRangeScope"/>: there the payload is stepped over rather than
/// parsed, and its byte range recorded, so the response can carry the bytes themselves and parse them
/// once — into the caller's own type — instead of building a document nothing may ever read.
/// </summary>
/// <remarks>
/// A list or page result is the case this exists for: the client's only use of the payload is
/// <c>ScryJson.DeserializePayload</c>, which wants the projected rows and not a
/// <see cref="JsonDocument"/> of them. Parsing eagerly cost a copy of the payload's bytes and an index
/// over them, both discarded immediately; the serializer then had to write the element back out to a
/// buffer and re-read it to produce the rows.
/// </remarks>
sealed class PayloadConverter :
    JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (!PayloadRangeScope.Active)
        {
            return JsonDocument.ParseValue(ref reader).RootElement;
        }

        // The reader sits on the value's first token; Skip lands it on the last, so the span between
        // the two indices is exactly this payload and nothing around it. Both are offsets into the
        // buffer the deserialization began over, which is the memory the caller still holds — and that
        // memory is length-bounded by an int already, so neither offset can outrun the cast.
        var start = reader.TokenStartIndex;
        reader.Skip();
        PayloadRangeScope.Record((int) start, (int) reader.BytesConsumed);
        return default;
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
        value.WriteTo(writer);
}
