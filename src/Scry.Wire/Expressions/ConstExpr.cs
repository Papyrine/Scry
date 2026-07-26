namespace Scry.Wire;

/// <summary>
/// A literal constant. <see cref="Value"/> is the invariant-culture string form (null for a null
/// constant); the server reconciles it with the member type at the comparison site.
/// </summary>
public sealed record ConstExpr(string? Value, ClrTypeTag Tag) :
    Expr;