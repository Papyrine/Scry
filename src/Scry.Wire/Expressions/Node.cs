namespace Scry.Wire;

/// <summary>
/// A value expression used in predicates and projections. The set of node types is closed, so the
/// server can exhaustively validate every query — there is no way to encode an arbitrary method call.
/// </summary>
// begin-snippet: wireExpressions
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MemberNode), "member")]
[JsonDerivedType(typeof(ConstNode), "const")]
[JsonDerivedType(typeof(BinaryNode), "binary")]
[JsonDerivedType(typeof(UnaryNode), "unary")]
[JsonDerivedType(typeof(CallNode), "call")]
[JsonDerivedType(typeof(ConditionalNode), "conditional")]
[JsonDerivedType(typeof(AggregateNode), "aggregate")]
public abstract record Node;
// end-snippet