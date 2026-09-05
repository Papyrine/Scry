/// <summary>
/// Reads and writes a projection's members. A member that reads the member it is named for travels as
/// a bare string, and every other member as an object — and each has exactly one spelling, so a member
/// qualifying for the string arriving as an object is refused rather than taken as a synonym.
/// </summary>
/// <remarks>
/// A member reading its own name spells that name twice, once as the member's name and once as the
/// single-segment path of the node it wraps — nine lines of a formatted request carrying one token.
/// That is the shape every default projection is built from, so it is most of what a query writing no
/// <c>Select</c> sends. Refusing the other spelling is what keeps the short form a formatting choice
/// rather than a second encoding: one member has one form, so two requests meaning the same thing are
/// the same bytes — which is what everything keying off a request, from a cache to a fingerprint,
/// already assumes.
/// </remarks>
sealed class ProjectionMembersConverter :
    JsonConverter<IReadOnlyList<ProjectionMember>>
{
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ProjectionMember> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var member in value)
        {
            if (ReadsItsOwnName(member))
            {
                writer.WriteStringValue(member.Name);
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("name", member.Name);
            writer.WritePropertyName("value");
            // Written through the serializer rather than by hand so the value keeps its $type
            // discriminator; the property's static type is what the polymorphism is declared on.
            JsonSerializer.Serialize(writer, member.Value, options);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public override IReadOnlyList<ProjectionMember> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a projection's members as an array.");
        }

        var members = new List<ProjectionMember>();
        while (reader.Read() &&
               reader.TokenType != JsonTokenType.EndArray)
        {
            members.Add(ReadMember(ref reader, options));
        }

        return members;
    }

    static ProjectionMember ReadMember(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var read = reader.GetString()!;
            if (string.IsNullOrWhiteSpace(read))
            {
                throw new JsonException("A projection member written as a string names the member it reads, which cannot be blank.");
            }

            return new(read, new NodeValue(new MemberNode([read])));
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a projection member as a string, or as an object.");
        }

        string? name = null;
        ProjectionValue? value = null;
        while (reader.Read() &&
               reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a property of a projection member.");
            }

            var property = reader.GetString()!;
            reader.Read();
            if (string.Equals(property, "name", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("Expected a projection member's name as a string.");
                }

                name = reader.GetString();
                continue;
            }

            if (string.Equals(property, "value", StringComparison.OrdinalIgnoreCase))
            {
                value = JsonSerializer.Deserialize<ProjectionValue>(ref reader, options);
                continue;
            }

            // Refused, as every other request object refuses a member nothing reads.
            throw new JsonException($"A projection member does not carry '{property}'.");
        }

        if (name is null)
        {
            throw new JsonException("A projection member is missing its name.");
        }

        if (value is null)
        {
            throw new JsonException($"Projection member '{name}' is missing its value.");
        }

        var member = new ProjectionMember(name, value);
        if (ReadsItsOwnName(member))
        {
            throw new JsonException($"""A projection member reading the member it is named for is written as a string: "{name}".""");
        }

        return member;
    }

    static bool ReadsItsOwnName(ProjectionMember member) =>
        member.Value is NodeValue {Node: MemberNode {Path: [var single]}} &&
        string.Equals(single, member.Name, StringComparison.Ordinal);
}
