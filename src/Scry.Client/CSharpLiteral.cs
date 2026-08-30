/// <summary>
/// C# literal spelling for the strings and chars the renderer writes into a snippet. Control
/// characters and the line separators C# refuses inside a literal are escaped as <c>\uXXXX</c>;
/// other non-ASCII text is carried verbatim.
/// </summary>
static class CSharpLiteral
{
    public static string String(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            Append(builder, character, '"');
        }

        builder.Append('"');
        return builder.ToString();
    }

    public static string Char(char value)
    {
        var builder = new StringBuilder(4);
        builder.Append('\'');
        Append(builder, value, '\'');
        builder.Append('\'');
        return builder.ToString();
    }

    static void Append(StringBuilder builder, char character, char quote)
    {
        if (character == '\\')
        {
            builder.Append("\\\\");
            return;
        }

        if (character == quote)
        {
            builder.Append('\\').Append(quote);
            return;
        }

        if (character < 0x20 ||
            character is (char)0x85 or (char)0x2028 or (char)0x2029)
        {
            builder.Append("\\u").Append(((int) character).ToString("X4", CultureInfo.InvariantCulture));
            return;
        }

        builder.Append(character);
    }
}