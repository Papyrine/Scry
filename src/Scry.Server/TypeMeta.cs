using System.Diagnostics.CodeAnalysis;

/// <summary>The allow-listed surface of a queryable CLR type.</summary>
sealed class TypeMeta(Type clrType)
{
    public Type ClrType { get; } = clrType;

    /// <summary>
    /// The nearest allow-listed base type, when the CLR type derives from one. Its members are part of
    /// <see cref="Members"/> here — reflection reports inherited properties — but are described to
    /// tooling only once, on the base, so the generated models can inherit rather than repeat them.
    /// </summary>
    public Type? Base { get; set; }
    public Dictionary<string, Member> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Previous member names still accepted from clients generated before a rename, mapped to the
    /// member they now resolve to. Deliberately separate from <see cref="Members"/>, which is the
    /// current surface: introspection and the schema stamp describe that alone.
    /// </summary>
    public Dictionary<string, Member> PreviousNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The members forming the row's primary key, ordinal by name — the order attachment keys travel
    /// in. Null unless the type carries an <c>[Attachment]</c>, which is the only thing that fetches
    /// by key. Derived from the annotations and the naming conventions at build, then verified against
    /// the real EF key at startup, where a model exists to compare with.
    /// </summary>
    public IReadOnlyList<Member>? AttachmentKeys { get; set; }

    public bool TryGetMember(string name, [MaybeNullWhen(false)] out Member member) =>
        Members.TryGetValue(name, out member) ||
        PreviousNames.TryGetValue(name, out member);
}
