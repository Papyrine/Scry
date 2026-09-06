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
    // Integers stay readable: a payload carries a computed value as its number (a day of the week read
    // as an int) and a batch entry its status. A request's enums are checked for range by the
    // validator instead, which is where an undefined value is a rejection rather than a fault.
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
            // A number is read as the built-in converter reads it; a name is read here, exactly as
            // spelled. The built-in read matches names case-insensitively, which would make "equal"
            // and "Equal" two byte-strings for one query — and the ETag, the URL, and the audit
            // fingerprint are all over the bytes. A name is part of the wire contract, so one spelling.
            if (reader.TokenType != JsonTokenType.String)
            {
                return inner.Read(ref reader, typeToConvert, options);
            }

            var name = reader.GetString()!;
            if (Enum.TryParse<T>(name, ignoreCase: false, out var exact))
            {
                return exact;
            }

            if (EnumAliasScope.Current is not { } aliases)
            {
                throw new JsonException($"'{name}' is not a value of enum '{typeToConvert.Name}'. Enum names are case-sensitive on the wire.");
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
                    if (Enum.TryParse<T>(previous, ignoreCase: false, out var value))
                    {
                        return value;
                    }
                }
            }

            throw new ScryStaleClientException(
                $"'{name}' is not a value of enum '{typeToConvert.Name}' in this client's generated model. The server's model has changed — regenerate the client, or reload the deployed app.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            inner.Write(writer, value, options);
    }
}
