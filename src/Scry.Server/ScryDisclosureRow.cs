namespace Scry;

/// <summary>
/// A row host code says something it sent was made from: what a command's result was read from, what
/// an external disclosure carried. The disclosure audit names rows this way, so the record finds what
/// host code sent of a row beside what Scry's own answers sent of it.
/// </summary>
/// <param name="Source">
/// The source it belongs to, by wire name. On a server, the name of any type of a hierarchy is
/// recorded as the name of its top, as a query of it would be; elsewhere it is recorded as given, so
/// give the top's.
/// </param>
/// <param name="Key">
/// The primary-key values in the key's own order, each as the key member's own type would be written:
/// what <see cref="ScryDisclosureEntity.KeyOf"/> takes.
/// </param>
public sealed record ScryDisclosureRow(string Source, IReadOnlyList<object?> Key)
{
    /// <summary>The version the row was at, as the JSON value of the source's version member, where it has one.</summary>
    public string? Version { get; init; }

    // Where the row was named by its type, on a server that can name it from the schema.
    internal Type? ClrType { get; init; }

    /// <summary>
    /// Writes rows as the text a bus's completion message carries them in: plain JSON, so an endpoint's
    /// serializer never has to understand a Scry type. Null where there are none.
    /// </summary>
    public static string? Serialize(IEnumerable<ScryDisclosureRow> rows)
    {
        using var buffer = new PooledBufferWriter();
        var count = 0;
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var row in rows)
            {
                Validate(row);
                count++;
                json.WriteStartObject();
                json.WriteString(sourceProperty, row.Source);
                json.WritePropertyName(keyProperty);
                json.WriteRawValue(ScryDisclosureEntity.KeyOf(row.Key), skipInputValidation: true);
                if (row.Version is { } version)
                {
                    json.WriteString(versionProperty, version);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        if (count == 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer.WrittenMemory.Span);
    }

    /// <summary>
    /// Reads what <see cref="Serialize"/> wrote. Empty for null. Each key value is read back as the
    /// JSON it was written as, so the key is recorded as the same text.
    /// </summary>
    /// <exception cref="FormatException">The text is not rows <see cref="Serialize"/> wrote.</exception>
    public static IReadOnlyList<ScryDisclosureRow> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var rows = new List<ScryDisclosureRow>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var key = element
                    .GetProperty(keyProperty)
                    .EnumerateArray()
                    .Select(_ => (object?) _.Clone())
                    .ToList();
                string? version = null;
                if (element.TryGetProperty(versionProperty, out var written))
                {
                    version = written.GetString();
                }

                var row = new ScryDisclosureRow(element.GetProperty(sourceProperty).GetString() ?? "", key)
                {
                    Version = version
                };
                Validate(row);
                rows.Add(row);
            }

            return rows;
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new FormatException("The text is not a list of rows the disclosure audit wrote.", exception);
        }
    }

    internal static void Validate(ScryDisclosureRow row)
    {
        if (string.IsNullOrEmpty(row.Source) ||
            row.Key.Count == 0)
        {
            throw new ArgumentException("A row named to the disclosure audit has to name its source and its key: it is found by them.", nameof(row));
        }
    }

    const string sourceProperty = "source";
    const string keyProperty = "key";
    const string versionProperty = "version";
}
