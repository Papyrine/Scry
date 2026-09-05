// Linked into Scry.Server (net10.0) and Scry.SourceGenerator, so the two sides agree on exactly which
// source names are expressible. The generator also multi-targets netstandard2.0 — the target Roslyn
// loads as an analyzer — so this file must compile there too. The alias keeps it independent of what
// each project happens to carry in its global usings.

using Category = System.Globalization.UnicodeCategory;

/// <summary>
/// Whether a name can be written as a C# member name. A source name is not only a wire name: it is
/// also the property the generated <c>ScryQuery</c> exposes it as, and the property the explorer
/// synthesizes from introspection. One that is not an identifier emits code that does not compile, in
/// generated files the consumer cannot edit — so the generator reports it (SCRY003) and the server
/// refuses it at startup rather than either producing that code.
/// </summary>
/// <remarks>
/// The specification's identifier rules are spelled out here rather than deferred to Roslyn's
/// <c>SyntaxFacts</c>, which the server cannot reference. Erring strict is safe — a rejected name is
/// reported against the model, with the attribute to fix named — where erring loose would put
/// uncompilable code in front of a consumer, so the rules are stated rather than approximated. A
/// verbatim '@' prefix is deliberately not accepted: the same string is the wire name, which carries
/// no '@'.
/// </remarks>
static class CSharpIdentifier
{
    public static bool IsValid(string? name)
    {
        if (name is null or "" ||
            reserved.Contains(name))
        {
            return false;
        }

        if (!IsStart(name[0]))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            if (!IsPart(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The name as generated code writes it: prefixed with '@' where it is a reserved keyword, which
    /// is legal for a member and an enum value, and unchanged otherwise. The wire name is the bare one
    /// either way — the prefix is spelling, not identity — so a model written in a language that lets
    /// a member be called <c>event</c> generates a client that compiles.
    /// </summary>
    public static string Escape(string name)
    {
        if (reserved.Contains(name))
        {
            return "@" + name;
        }

        return name;
    }

    // Lu, Ll, Lt, Lm, Lo and Nl, plus the underscore.
    static bool IsStart(char character) =>
        character == '_' ||
        char.IsLetter(character) ||
        char.GetUnicodeCategory(character) == Category.LetterNumber;

    // The start categories plus Nd, Mn, Mc, Pc and Cf. Pc is what covers the underscore here.
    static bool IsPart(char character) =>
        char.IsLetterOrDigit(character) ||
        char.GetUnicodeCategory(character) is
            Category.LetterNumber or
            Category.NonSpacingMark or
            Category.SpacingCombiningMark or
            Category.ConnectorPunctuation or
            Category.Format;

    /// <summary>
    /// The reserved keywords, which need an '@' to be written as a member name and so cannot be a
    /// source name. Contextual keywords (<c>var</c>, <c>record</c>, <c>where</c>, …) are deliberately
    /// absent: they are legal member names, and refusing one would reject a name C# is happy to
    /// express. <c>CSharpIdentifierTests</c> pins the list against Roslyn's own.
    /// </summary>
    static readonly HashSet<string> reserved = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
        // Undocumented, but reserved all the same.
        "__arglist", "__makeref", "__reftype", "__refvalue"
    };
}
