using System.Diagnostics.CodeAnalysis;

/// <summary>
/// A row's key as somebody types it, read into the values the record holds a row by.
/// </summary>
/// <remarks>
/// The record keeps a key as the JSON array of its values, each written as a response writes a value
/// of that type. What is typed is read the same way: as that array, or as the values of one with the
/// brackets left off, or — where it is neither — as one piece of text, which is what an identifier
/// pasted in from somewhere else usually is.
/// </remarks>
static class DisclosureKeys
{
    public const string Help = "A key is its values in the key's own order, separated by commas: numbers as they are and text in quotes, as 5 or \"A\", 7.";

    public static bool TryRead(string? text, [NotNullWhen(true)] out IReadOnlyList<object?>? values)
    {
        values = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith('[') &&
            TryArray(text, out values))
        {
            return true;
        }

        if (TryArray($"[{text}]", out values))
        {
            return true;
        }

        // A GUID, a date, a code: text that was never going to be JSON.
        values = [text];
        return true;
    }

    static bool TryArray(string json, [NotNullWhen(true)] out IReadOnlyList<object?>? values)
    {
        values = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var read = new List<object?>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!TryValue(element, out var value))
                {
                    return false;
                }

                read.Add(value);
            }

            values = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Each as the type that writes back the same text: a whole number as a long, any other as the
    // decimal that keeps its digits. A key is never an object or a list.
    static bool TryValue(JsonElement element, out object? value)
    {
        value = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var whole))
                {
                    value = whole;
                    return true;
                }

                if (element.TryGetDecimal(out var exact))
                {
                    value = exact;
                    return true;
                }

                value = element.GetDouble();
                return true;
            default:
                return false;
        }
    }
}
