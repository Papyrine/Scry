namespace Scry.Wire;

/// <summary>A call to one of the closed set of <see cref="KnownFunction"/>s.</summary>
public sealed record CallExpr(KnownFunction Function, Expr Target, IReadOnlyList<Expr> Arguments) :
    Expr;