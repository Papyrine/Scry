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
/// A navigation path of allow-listed property names, e.g. <c>"Name"</c> or
/// <c>["Manager", "Name"]</c>. Each segment is validated against the allow-list of the type reached
/// so far.
/// </summary>
public sealed record MemberNode(
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path) :
    Node;

/// <summary>
/// The element of the collection a <see cref="SubqueryNode"/> is reading, used where a
/// <see cref="MemberNode"/> would be over a collection of rows. A collection of <i>values</i> — an EF
/// primitive collection, typically a JSON column — has no member to name, so this is how its element
/// is read: <c>Tags.Any(_ =&gt; _ == "urgent")</c> is a binary node over this and a constant, and
/// <c>Scores.Sum()</c> is a sum whose selector is this.
/// </summary>
/// <remarks>
/// Only meaningful where the row being read is a value rather than an allow-listed type, which is
/// exactly inside a subquery over a collection of scalars. Anywhere else the server rejects it: it
/// would otherwise name a whole entity, which is not something a query may compare, order by, or
/// project.
/// </remarks>
public sealed record ElementNode :
    Node;

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

/// <summary>A binary operation over two expressions.</summary>
public sealed record BinaryNode(BinaryOp Op, Node Left, Node Right) :
    Node;

/// <summary>A unary operation over one expression.</summary>
public sealed record UnaryNode(UnaryOp Op, Node Operand) :
    Node;

/// <summary>A call to one of the closed set of <see cref="KnownFunction"/>s.</summary>
public sealed record CallNode(KnownFunction Function, Node Target, IReadOnlyList<Node> Arguments) :
    Node;

/// <summary>A conditional expression (<c>test ? ifTrue : ifFalse</c>).</summary>
public sealed record ConditionalNode(Node Test, Node IfTrue, Node IfFalse) :
    Node;

/// <summary>
/// A question asked about a collection navigation, evaluated by the database as a correlated
/// subquery. <see cref="Path"/> names the collection — reference navigations may precede it, the
/// collection is always the last segment. <see cref="Predicate"/> and <see cref="Selector"/> read the
/// collection's <i>element</i>, not the row the subquery hangs off.
/// </summary>
/// <remarks>
/// The result is always a scalar, so a subquery can appear anywhere a value can and never widens the
/// shape of a response. A subquery may not appear inside another subquery, nor inside a membership
/// test against another source (<see cref="InSourceNode"/>): either would compound its cost per element.
/// </remarks>
public sealed record SubqueryNode(
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path,
    SubqueryFn Function,
    Node? Predicate = null,
    Node? Selector = null) :
    Node;

/// <summary>
/// Reads a string value under a particular case sensitivity, so the comparisons wrapping it are made
/// that way. Composes rather than doubling the function set: <c>Contains(Collate(Name, …), term)</c>.
/// </summary>
/// <remarks>
/// The node carries a <see cref="StringMatch"/>, not a collation name. A collation cannot be a query
/// parameter — it is emitted into the SQL text — so the string that implements each intent comes from
/// server configuration and never from a request. A server that has configured none rejects the node.
/// </remarks>
public sealed record CollateNode(Node Target, StringMatch Match) :
    Node;

/// <summary>
/// Membership of a set drawn from another source — SQL <c>IN (SELECT …)</c>. <see cref="Value"/> reads
/// the row being tested; <see cref="Selector"/> and <see cref="Predicate"/> read a row of
/// <see cref="Root"/>, which is a source name exactly as a request's own root is.
/// </summary>
/// <remarks>
/// The named source is resolved and <b>policy-filtered</b> independently before the test, the same way
/// a <see cref="JoinOp"/> resolves its second side. Membership can therefore only ever be of rows the
/// caller could have queried directly: a row the source's policy hides is not in the set, so the test
/// cannot be used to learn that it exists. A membership test may not appear inside another, nor inside
/// a <see cref="SubqueryNode"/>; <see cref="Value"/> alone may carry a subquery, since it reads the
/// row being tested rather than a row of the set.
/// </remarks>
public sealed record InSourceNode(
    Node Value,
    string Root,
    Node Selector,
    Node? Predicate = null) :
    Node;

/// <summary>
/// An aggregate over a group. <see cref="Selector"/> is the member being aggregated (null for
/// <see cref="AggregateFn.Count"/>); <see cref="Separator"/> is carried by
/// <see cref="AggregateFn.Join"/> alone. Only valid inside a projection that follows a group
/// operator.
/// </summary>
public sealed record AggregateNode(AggregateFn Function, Node? Selector = null, string? Separator = null) :
    Node
{
    /// <summary>
    /// Filters the group's rows before the fold — <c>g.Where(pred).Sum(…)</c>, or the
    /// <c>Count(pred)</c> that abbreviates it. Read against the group's element. Travels under wire
    /// version 2, so a server predating it rejects the request rather than folding unfiltered.
    /// </summary>
    public Node? Predicate { get; init; }

    /// <summary>
    /// Folds only the distinct selected values — <c>g.Select(sel).Distinct().Agg()</c>. Requires a
    /// <see cref="Selector"/>, <see cref="AggregateFn.Count"/> included, which is the one shape Count
    /// carries one in. Travels under wire version 2, like <see cref="Predicate"/>; omitted from the
    /// wire when false, so a version-1 request keeps its exact bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Distinct { get; init; }
}

/// <summary>
/// The key a query grouped by, read inside the projection or group filter that follows. Only valid
/// there. <see cref="Index"/> selects the part of a composite key, and is zero for a single one.
/// </summary>
/// <remarks>
/// A key that is a plain member is named by its own <see cref="MemberNode"/> instead — the path is
/// what the server matches it back by, and saying it that way keeps an existing client's requests
/// unchanged. This node exists for the keys that have no path to name: a key computed from an
/// expression, where the only thing to say is which of the query's keys is meant.
/// </remarks>
public sealed record GroupKeyNode(int Index) :
    Node;

/// <summary>
/// Several join keys compared as one: the sides match when every part matches, position by position.
/// Only valid as a <see cref="JoinOp"/> key — a composite has no value of its own, so it can appear
/// nowhere a value can. A server predating this node rejects the request at deserialization rather
/// than joining on less than the whole key.
/// </summary>
public sealed record CompositeKeyNode(IReadOnlyList<Node> Parts) :
    Node;
