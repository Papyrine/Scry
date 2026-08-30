/// <summary>Pretty-printing for the sidecar's request and response panes.</summary>
static class SidecarJson
{
    static readonly JsonSerializerOptions indented = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Re-serializes JSON with indentation for display. Anything that does not parse — a proxy's
    /// HTML error page, say — is shown as it arrived rather than hidden behind a failure.
    /// </summary>
    public static string Prettify(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static string Prettify(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.Serialize(document.RootElement, indented);
        }
        catch (JsonException)
        {
            return Encoding.UTF8.GetString(utf8);
        }
    }
}
