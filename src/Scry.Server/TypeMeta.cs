using System.Diagnostics.CodeAnalysis;

/// <summary>The allow-listed surface of a queryable CLR type.</summary>
sealed class TypeMeta(Type clrType)
{
    public Type ClrType { get; } = clrType;
    public Dictionary<string, Member> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Previous member names still accepted from clients generated before a rename, mapped to the
    /// member they now resolve to. Deliberately separate from <see cref="Members"/>, which is the
    /// current surface: introspection and the schema stamp describe that alone.
    /// </summary>
    public Dictionary<string, Member> PreviousNames { get; } = new(StringComparer.Ordinal);

    public bool TryGetMember(string name, [MaybeNullWhen(false)] out Member member) =>
        Members.TryGetValue(name, out member) ||
        PreviousNames.TryGetValue(name, out member);
}
