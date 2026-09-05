/// <summary>
/// Reads every <see cref="IReadOnlyList{T}"/> a request carries, refusing a null element.
/// <c>RespectNullableAnnotations</c> refuses a null where a member is declared to hold a value, but
/// says nothing about the elements of a collection, so <c>"pipeline": [null]</c> would otherwise
/// arrive as a list holding a null for the validator to dereference. Writing is byte-identical to the
/// serializer's own.
/// </summary>
sealed class NonNullElementsConverterFactory :
    JsonConverterFactory
{
    // The request vocabulary only. A response is the server's own, and a batch response's entries are
    // read with a range scope that needs the reader they arrived on rather than one scoped to a value.
    static readonly HashSet<Type> elements =
    [
        typeof(QueryOp),
        typeof(Node),
        typeof(JoinMember),
        typeof(ProjectionMember),
        typeof(QueryRequest),
        typeof(AttachmentKey)
    ];

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) &&
        elements.Contains(typeToConvert.GetGenericArguments()[0]);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(NonNullElementsConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    sealed class NonNullElementsConverter<T> :
        JsonConverter<IReadOnlyList<T>>
        where T : class
    {
        public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"Expected an array of {typeof(T).Name}.");
            }

            var items = new List<T>();
            while (reader.Read() &&
                   reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    throw new JsonException($"An element of an array of {typeof(T).Name} cannot be null.");
                }

                items.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
            }

            return items;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                JsonSerializer.Serialize(writer, item, options);
            }

            writer.WriteEndArray();
        }
    }
}
