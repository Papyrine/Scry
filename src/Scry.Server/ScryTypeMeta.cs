namespace Scry;

/// <summary>The allow-listed surface of a queryable CLR type.</summary>
sealed class ScryTypeMeta(Type clrType)
{
    public Type ClrType { get; } = clrType;
    public Dictionary<string, ScryMember> Members { get; } = new(StringComparer.Ordinal);
}
