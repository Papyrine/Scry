namespace Scry.Wire;

/// <summary>
/// A value expression used in predicates and projections. The set of node types is closed, so the
/// server can exhaustively validate every query — there is no way to encode an arbitrary method call.
/// </summary>
// begin-snippet: wireExpressions
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MemberExpr), "member")]
[JsonDerivedType(typeof(ConstExpr), "const")]
[JsonDerivedType(typeof(BinaryExpr), "binary")]
[JsonDerivedType(typeof(UnaryExpr), "unary")]
[JsonDerivedType(typeof(CallExpr), "call")]
[JsonDerivedType(typeof(AggregateExpr), "aggregate")]
public abstract record Expr;
// end-snippet