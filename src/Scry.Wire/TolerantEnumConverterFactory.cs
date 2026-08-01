/// <summary>
/// Wraps <see cref="JsonStringEnumConverter"/> for every enum, byte-identical on write and on every
/// successful read. On an unrecognised value name it consults <see cref="EnumAliasScope"/>: inside a
/// payload read, the name is resolved through the response's enum aliases to a previous name this
/// client's enum does have; failing that it reports a stale client instead of a bare JsonException.
/// Outside a payload the original exception propagates, so request parsing keeps failing closed.
/// </summary>
sealed class TolerantEnumConverterFactory :
    JsonConverterFactory
{
    static readonly JsonStringEnumConverter inner = new();

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert),
            inner.CreateConverter(typeToConvert, options))!;

    sealed class TolerantEnumConverter<T>(JsonConverter innerConverter) :
        JsonConverter<T>
        where T : struct, Enum
    {
        readonly JsonConverter<T> inner = (JsonConverter<T>)innerConverter;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // The value is a single token, so the checkpoint doubles as the correct final reader
            // position: converters return with the reader on the last token of the value.
            var checkpoint = reader;
            try
            {
                return inner.Read(ref reader, typeToConvert, options);
            }
            catch (JsonException)
            {
                if (EnumAliasScope.Current is not { } aliases)
                {
                    throw;
                }

                reader = checkpoint;
                if (reader.TokenType != JsonTokenType.String ||
                    reader.GetString() is not { } name)
                {
                    throw;
                }

                foreach (var alias in aliases)
                {
                    if (alias.EnumName != typeToConvert.Name ||
                        alias.ValueName != name)
                    {
                        continue;
                    }

                    foreach (var previous in alias.PreviousNames)
                    {
                        if (Enum.TryParse<T>(previous, out var value))
                        {
                            return value;
                        }
                    }
                }

                throw new ScryStaleClientException(
                    $"'{name}' is not a value of enum '{typeToConvert.Name}' in this client's generated model. The server's model has changed — regenerate the client, or reload the deployed app.");
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            inner.Write(writer, value, options);
    }
}
