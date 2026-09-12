namespace Scry;

/// <summary>
/// A value expression used in predicates and projections. The set of node types is closed, so the
/// server can exhaustively validate every query — there is no way to encode an arbitrary method call.
/// </summary>
// begin-snippet: wireExpressions
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MemberNode), "member")]
[JsonDerivedType(typeof(ElementNode), "element")]
[JsonDerivedType(typeof(ConstNode), "const")]
[JsonDerivedType(typeof(BinaryNode), "binary")]
[JsonDerivedType(typeof(UnaryNode), "unary")]
[JsonDerivedType(typeof(CallNode), "call")]
[JsonDerivedType(typeof(ConditionalNode), "conditional")]
[JsonDerivedType(typeof(SubqueryNode), "subquery")]
[JsonDerivedType(typeof(CollateNode), "collate")]
[JsonDerivedType(typeof(InSourceNode), "inSource")]
[JsonDerivedType(typeof(AggregateNode), "aggregate")]
[JsonDerivedType(typeof(GroupKeyNode), "groupKey")]
[JsonDerivedType(typeof(CompositeKeyNode), "compositeKey")]
public closed record Node;
// end-snippet

/// <summary>
/// A literal constant. <see cref="Value"/> is the invariant-culture string form (null for a null
/// constant); the server reconciles it with the member type at the comparison site.
/// </summary>
public sealed record ConstNode(string? Value, ClrTypeTag Tag) :
    Node
{
    // The wire's constructor: only the members a request has to carry. The value may be absent, and
    // reaches the reader through its init accessor instead, since an optional parameter would have to
    // trail and the declared order is the one callers write.
    [JsonConstructor]
    public ConstNode(ClrTypeTag tag) :
        this(null, tag)
    {
    }
}