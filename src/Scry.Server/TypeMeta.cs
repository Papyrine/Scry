/// <summary>The allow-listed surface of a queryable CLR type.</summary>
sealed class TypeMeta(Type clrType)
{
    public Type ClrType { get; } = clrType;
    public Dictionary<string, Member> Members { get; } = new(StringComparer.Ordinal);
}
