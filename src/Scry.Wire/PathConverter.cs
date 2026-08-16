/// <summary>
/// Reads and writes a member path. A path naming a single member travels as a bare string, and one
/// naming any other number of members as an array — and each length has exactly one spelling, so a
/// single-segment path arriving as a one-element array is refused rather than taken as a synonym.
/// </summary>
/// <remarks>
/// Most paths on the wire name one member, where the array around a lone string is three lines of a
/// formatted request saying nothing. Refusing the other spelling is what keeps that a formatting
/// choice rather than a second encoding: one path has one form, so two requests meaning the same
/// thing are the same bytes — which is what everything keying off a request, from a cache to a
/// fingerprint, already assumes.
/// </remarks>
sealed class PathConverter :
    JsonConverter<IReadOnlyList<string>>
{
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var segment in value)
        {
            writer.WriteStringValue(segment);
        }

        writer.WriteEndArray();
    }

    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a member path as a string, or as an array of strings.");
        }

        var segments = new List<string>();
        while (reader.Read() &&
               reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected every segment of a member path to be a string.");
            }

            segments.Add(reader.GetString()!);
        }

        if (segments.Count == 1)
        {
            throw new JsonException($"""A member path naming one member is written as a string: "{segments[0]}", not ["{segments[0]}"].""");
        }

        return segments;
    }
}
