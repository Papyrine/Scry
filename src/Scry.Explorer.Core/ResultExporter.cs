namespace Scry;

/// <summary>
/// Renders a result set as the three formats the explorer offers for download: the rows as the table
/// is showing them, in the same order.
/// </summary>
/// <remarks>
/// <see cref="Csv"/> takes the rendered cells because a grid is what it is a copy of; the other two
/// take the server's own rows, so a projection into a navigation stays nested rather than being
/// flattened away.
/// </remarks>
public static class ResultExporter
{
    static readonly JsonSerializerOptions indented = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// The rows as CSV — RFC 4180 quoted, header row first. A cell a spreadsheet would read as a
    /// formula is neutralised: see <see cref="CsvField"/>.
    /// </summary>
    public static string Csv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columns.Select(CsvField)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(CsvField)));
        }

        return builder.ToString();
    }

    /// <summary>The rows as JSON, exactly as the server sent them.</summary>
    public static string Json(IReadOnlyList<JsonElement> rows) =>
        JsonSerializer.Serialize(rows, indented);

    /// <summary>The rows as XML — a <c>row</c> element each, with a child element per member.</summary>
    public static string Xml(IReadOnlyList<JsonElement> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        builder.AppendLine("<results>");
        foreach (var row in rows)
        {
            WriteXml(builder, "row", row, depth: 1);
        }

        builder.Append("</results>");
        return builder.ToString();
    }

    // RFC 4180: a field containing a comma, a quote, or a newline is quoted, and quotes inside it are
    // doubled. Everything else is written as-is — except a field a spreadsheet would execute.
    //
    // Excel, and the others, read a cell beginning with '=', '+', '-', '@', a tab, or a carriage
    // return as a formula rather than as text, and a formula can reach outside the sheet. The rows
    // are database content, which is routinely whatever an end user typed into a form, and the
    // export is aimed at exactly those spreadsheets. So such a field is prefixed with an apostrophe,
    // which every one of them takes as "text follows" and none of them displays. A number is exempt:
    // "-5" is a value a spreadsheet should keep computing with, and no formula is a number.
    static string CsvField(string value)
    {
        if (value.Length > 0 &&
            value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    static void WriteXml(StringBuilder builder, string name, JsonElement value, int depth)
    {
        var indent = new string(' ', depth * 2);

        // An absent value stays an empty element rather than being dropped, so every row keeps the
        // same shape.
        if (value.ValueKind == JsonValueKind.Null)
        {
            builder.Append(indent).Append('<').Append(name).AppendLine(" />");
            return;
        }

        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            builder.Append(indent).Append('<').Append(name).Append('>')
                .Append(XmlText(value.ToString()))
                .Append("</").Append(name).AppendLine(">");
            return;
        }

        builder.Append(indent).Append('<').Append(name).AppendLine(">");
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                WriteXml(builder, XmlName(property.Name), property.Value, depth + 1);
            }
        }
        else
        {
            foreach (var item in value.EnumerateArray())
            {
                WriteXml(builder, "item", item, depth + 1);
            }
        }

        builder.Append(indent).Append("</").Append(name).AppendLine(">");
    }

    // Text content: the three characters that cannot appear literally are escaped, and the control
    // characters XML 1.0 forbids outright are dropped — a value that came out of a column should not
    // be able to produce a document no parser will open.
    static string XmlText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    if (character is '\t' or '\n' or '\r' or >= ' ')
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    // Member names come from the caller's own C# identifiers, so they already are valid XML names —
    // but the rows are the server's response, and an export should never be able to emit a name that
    // does not parse. Anything outside a name character becomes '_'.
    static string XmlName(string name)
    {
        if (name.Length == 0)
        {
            return "_";
        }

        var builder = new StringBuilder(name.Length);
        builder.Append(char.IsLetter(name[0]) || name[0] == '_' ? name[0] : '_');
        foreach (var character in name.AsSpan(1))
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_');
        }

        return builder.ToString();
    }
}
