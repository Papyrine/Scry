using System.Diagnostics.CodeAnalysis;

/// <summary>
/// A row's key as somebody types it, read into the values the record holds a row by.
/// </summary>
/// <remarks>
/// The record keeps a key as the JSON array of its values, each written as a response writes a value
/// of that type. What is typed is read the same way: as that array, or as the values of one with the
/// brackets left off, or — where it is neither — as one piece of text, which is what an identifier
/// pasted in from somewhere else usually is. The explorer has no model to say what type a key is, so
/// a piece of text is read as every spelling it could be: as typed, and as the server writes what it
/// parses as — a GUID in lowercase with hyphens, a date in ISO form — and the row is looked up under
/// each of them.
/// </remarks>
static class DisclosureKeys
{
    public const string Help = "A key is its values in the key's own order, separated by commas: numbers as they are and text in quotes, as 5 or \"A\", 7. A GUID or a date is found however it is spelled.";

    /// <summary>
    /// Reads what was typed into every key it could be. The first is the text as typed; the rest are
    /// the ways the record may hold what the text parses as. No two are the same text.
    /// </summary>
    public static bool TryRead(string? text, [NotNullWhen(true)] out IReadOnlyList<IReadOnlyList<object?>>? spellings)
    {
        spellings = null;
        if (!TryValues(text, out var values))
        {
            return false;
        }

        spellings = Spellings(values);
        return true;
    }

    /// <summary>Whether a key typed again is the key: the two share a spelling.</summary>
    public static bool Same(IReadOnlyList<IReadOnlyList<object?>> key, IReadOnlyList<IReadOnlyList<object?>> again) =>
        key.Select(ScryDisclosureEntity.KeyOf)
            .Intersect(again.Select(ScryDisclosureEntity.KeyOf))
            .Any();

    static bool TryValues(string? text, [NotNullWhen(true)] out IReadOnlyList<object?>? values)
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

    // Every key the typed values could be: each value as each thing it could be, combined, with the
    // values as typed first. A key of one value has as many spellings as that value; one of several
    // has every combination, which for a key of two GUIDs is four.
    static IReadOnlyList<IReadOnlyList<object?>> Spellings(IReadOnlyList<object?> values)
    {
        List<IReadOnlyList<object?>> spellings = [[]];
        foreach (var value in values)
        {
            var alternatives = Alternatives(value);
            spellings = [.. spellings.SelectMany(spelled => alternatives.Select(IReadOnlyList<object?> (alternative) => [.. spelled, alternative]))];
        }

        return [.. spellings.DistinctBy(ScryDisclosureEntity.KeyOf)];
    }

    // What one typed value could be. A number, a boolean or null is itself: the record writes each
    // one way. A piece of text is itself, and whatever the server writes differently from how it was
    // typed: a GUID, written in lowercase with hyphens, and a date or a date and time, written in
    // ISO form — with its zone where one was typed, since a key that carries an offset is written
    // with it.
    static List<object?> Alternatives(object? value)
    {
        if (value is not string text)
        {
            return [value];
        }

        var found = new List<object?>
        {
            text
        };
        if (Guid.TryParse(text, out var guid))
        {
            found.Add(guid);
        }

        // In ISO order only, so that a number quoted as text is not read as a year.
        if (text.Length >= 10 &&
            text[4] == '-' &&
            text[7] == '-' &&
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var moment))
        {
            if (moment.Kind == DateTimeKind.Local)
            {
                // Typed with an offset: a DateTimeOffset key is written with it, and the local time the
                // DateTime was made into is this machine's rather than the record's.
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var zoned))
                {
                    found.Add(zoned);
                }

                return found;
            }

            found.Add(moment);
            if (moment.Kind == DateTimeKind.Utc)
            {
                found.Add(new DateTimeOffset(moment));
            }
        }

        return found;
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
