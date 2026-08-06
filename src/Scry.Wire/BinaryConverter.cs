/// <summary>
/// Reads and writes <c>byte[]</c> payload values. Writing is byte-identical to the serializer's
/// built-in handling — a base64 string — so nothing changes when a response carries no multipart
/// parts. Reading additionally accepts the <c>{"$bin":n}</c> placeholder a multipart response leaves
/// where a diverted value was, resolving it against <see cref="BinaryPartScope"/>. A placeholder
/// outside a part-carrying deserialization, or one whose index has no part, fails closed.
/// </summary>
sealed class BinaryConverter :
    JsonConverter<byte[]>
{
    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteBase64StringValue(value);

    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetBytesFromBase64();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a base64 string or a {ScryBinary.PartProperty} placeholder for a byte[] value.");
        }

        if (!reader.Read() ||
            reader.TokenType != JsonTokenType.PropertyName ||
            reader.GetString() != ScryBinary.PartProperty)
        {
            throw new JsonException($"Expected a single {ScryBinary.PartProperty} property in a binary placeholder.");
        }

        // Read as an Int32 rather than asked for one: a number the index cannot be — fractional, or
        // past int range — would otherwise leave the reader to raise a FormatException that reaches the
        // caller as a JsonException with none of this said in it.
        if (!reader.Read() ||
            reader.TokenType != JsonTokenType.Number ||
            !reader.TryGetInt32(out var index))
        {
            throw new JsonException($"Expected a part index as the value of {ScryBinary.PartProperty}.");
        }

        if (!reader.Read() ||
            reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException($"Expected a binary placeholder to carry only {ScryBinary.PartProperty}.");
        }

        var parts = BinaryPartScope.Current ??
                    throw new JsonException($"A {ScryBinary.PartProperty} placeholder arrived outside a response carrying binary parts.");
        if (index < 0 ||
            index >= parts.Count)
        {
            throw new JsonException($"A {ScryBinary.PartProperty} placeholder references part {index}, but the response carried {parts.Count} parts.");
        }

        return parts[index];
    }
}
