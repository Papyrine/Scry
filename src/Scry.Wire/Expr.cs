using System.Text.Json.Serialization;

namespace Scry.Wire;

/// <summary>
/// A value expression used in predicates and projections. The set of node types is closed, so the
/// server can exhaustively validate every query — there is no way to encode an arbitrary method call.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MemberExpr), "member")]
[JsonDerivedType(typeof(ConstExpr), "const")]
[JsonDerivedType(typeof(BinaryExpr), "binary")]
[JsonDerivedType(typeof(UnaryExpr), "unary")]
[JsonDerivedType(typeof(CallExpr), "call")]
[JsonDerivedType(typeof(AggregateExpr), "aggregate")]
public abstract record Expr;

/// <summary>
/// A navigation path of allow-listed property names, e.g. <c>["Manager", "Name"]</c>. Each segment
/// is validated against the allow-list of the type reached so far.
/// </summary>
public sealed record MemberExpr(IReadOnlyList<string> Path) :
    Expr;

/// <summary>
/// A literal constant. <see cref="Value"/> is the invariant-culture string form (null for a null
/// constant); the server reconciles it with the member type at the comparison site.
/// </summary>
public sealed record ConstExpr(string? Value, ClrTypeTag Tag) :
    Expr;

/// <summary>A binary operation over two expressions.</summary>
public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) :
    Expr;

/// <summary>A unary operation over one expression.</summary>
public sealed record UnaryExpr(UnaryOp Op, Expr Operand) :
    Expr;

/// <summary>A call to one of the closed set of <see cref="KnownFunction"/>s.</summary>
public sealed record CallExpr(KnownFunction Function, Expr Target, IReadOnlyList<Expr> Arguments) :
    Expr;

/// <summary>
/// An aggregate over a group. <see cref="Selector"/> is the member being aggregated (null for
/// <see cref="AggregateFn.Count"/>). Only valid inside a projection that follows a group operator.
/// </summary>
public sealed record AggregateExpr(AggregateFn Function, Expr? Selector) :
    Expr;
