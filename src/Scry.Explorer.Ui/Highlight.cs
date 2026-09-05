using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

/// <summary>
/// Colorizes the read-only JSON and SQL panes. Every token is HTML-encoded before being wrapped, so
/// the panes stay safe to render an arbitrary server response; the spans add color without changing
/// the text content — which is what the UI tests assert on.
/// </summary>
static class Highlight
{
    static readonly HashSet<string> sqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AS", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "BETWEEN",
        "EXISTS", "ORDER", "GROUP", "BY", "HAVING", "ASC", "DESC", "TOP", "DISTINCT", "OFFSET",
        "FETCH", "NEXT", "FIRST", "ROW", "ROWS", "ONLY", "INNER", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "JOIN", "ON", "APPLY", "UNION", "ALL", "CASE", "WHEN", "THEN", "ELSE", "END",
        "CAST", "CONVERT", "COALESCE", "COUNT", "SUM", "MIN", "MAX", "AVG"
    };

    /// <summary>
    /// JSON with property names, strings, numbers, and keywords colored. Anything that does not
    /// parse as JSON (an error body, say) is rendered encoded but uncolored.
    /// </summary>
    public static MarkupString Json(string text)
    {
        if (!IsJson(text))
        {
            return new(WebUtility.HtmlEncode(text));
        }

        var builder = new StringBuilder(text.Length * 2);
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '"')
            {
                index = JsonString(builder, text, index);
                continue;
            }

            if (character == '-' ||
                char.IsAsciiDigit(character))
            {
                var end = index + 1;
                while (end < text.Length &&
                       (char.IsAsciiDigit(text[end]) || text[end] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    end++;
                }

                index = Append(builder, text, index, end, "tok-num");
                continue;
            }

            if (char.IsAsciiLetter(character))
            {
                // Outside a string, a bare word in valid JSON is true, false, or null.
                index = Append(builder, text, index, WordEnd(text, index), "tok-kw");
                continue;
            }

            AppendEncoded(builder, character);
            index++;
        }

        return new(builder.ToString());
    }

    /// <summary>SQL with keywords, string literals, and numbers colored.</summary>
    public static MarkupString Sql(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\'')
            {
                index = SqlString(builder, text, index);
                continue;
            }

            if (char.IsAsciiDigit(character))
            {
                var end = index + 1;
                while (end < text.Length &&
                       (char.IsAsciiDigit(text[end]) || text[end] == '.'))
                {
                    end++;
                }

                index = Append(builder, text, index, end, "tok-num");
                continue;
            }

            if (char.IsAsciiLetter(character))
            {
                var end = WordEnd(text, index);
                if (sqlKeywords.Contains(text[index..end]))
                {
                    index = Append(builder, text, index, end, "tok-kw");
                }
                else
                {
                    builder.Append(WebUtility.HtmlEncode(text[index..end]));
                    index = end;
                }

                continue;
            }

            AppendEncoded(builder, character);
            index++;
        }

        return new(builder.ToString());
    }

    static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The whole string token, colored as a property name when the next non-space character is a
    // colon, and as a value otherwise.
    static int JsonString(StringBuilder builder, string text, int start)
    {
        var end = start + 1;
        while (end < text.Length &&
               text[end] != '"')
        {
            // An escape consumes the character after it, which is what keeps \" inside the token.
            end += text[end] == '\\' ? 2 : 1;
        }

        var stop = Math.Min(end + 1, text.Length);
        string kind;
        if (IsKey(text, stop))
        {
            kind = "tok-key";
        }
        else
        {
            kind = "tok-str";
        }

        return Append(builder, text, start, stop, kind);
    }

    static bool IsKey(string text, int index)
    {
        var next = index;
        while (next < text.Length &&
               text[next] == ' ')
        {
            next++;
        }

        return next < text.Length &&
               text[next] == ':';
    }

    // 'literal', with '' as the embedded-quote escape.
    static int SqlString(StringBuilder builder, string text, int start)
    {
        var end = start + 1;
        while (end < text.Length)
        {
            if (text[end] == '\'')
            {
                if (end + 1 < text.Length &&
                    text[end + 1] == '\'')
                {
                    end += 2;
                    continue;
                }

                end++;
                break;
            }

            end++;
        }

        return Append(builder, text, start, end, "tok-str");
    }

    static int WordEnd(string text, int start)
    {
        var end = start;
        while (end < text.Length &&
               char.IsAsciiLetter(text[end]))
        {
            end++;
        }

        return end;
    }

    static int Append(StringBuilder builder, string text, int start, int end, string className)
    {
        builder
            .Append("<span class=\"")
            .Append(className)
            .Append("\">")
            .Append(WebUtility.HtmlEncode(text[start..end]))
            .Append("</span>");
        return end;
    }

    static void AppendEncoded(StringBuilder builder, char character)
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
                builder.Append(character);
                break;
        }
    }
}
