namespace Scry;

/// <summary>
/// A single operator in the query pipeline, applied left-to-right. The set is closed; the server
/// validates pipeline well-formedness (e.g. <c>ThenBy</c> only after <c>OrderBy</c>, aggregates only
/// in a projection following <c>GroupBy</c>, at most one terminal).
/// </summary>
// begin-snippet: wireOperators
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WhereOp), "where")]
[JsonDerivedType(typeof(OrderByOp), "orderBy")]
[JsonDerivedType(typeof(ThenByOp), "thenBy")]
[JsonDerivedType(typeof(SkipOp), "skip")]
[JsonDerivedType(typeof(TakeOp), "take")]
[JsonDerivedType(typeof(SelectOp), "select")]
[JsonDerivedType(typeof(SelectManyOp), "selectMany")]
[JsonDerivedType(typeof(OfTypeOp), "ofType")]
[JsonDerivedType(typeof(GroupByOp), "groupBy")]
[JsonDerivedType(typeof(DistinctOp), "distinct")]
[JsonDerivedType(typeof(ReverseOp), "reverse")]
[JsonDerivedType(typeof(JoinOp), "join")]
[JsonDerivedType(typeof(SetOp), "set")]
[JsonDerivedType(typeof(CountOp), "count")]
[JsonDerivedType(typeof(LongCountOp), "longCount")]
[JsonDerivedType(typeof(AnyOp), "any")]
[JsonDerivedType(typeof(AllOp), "all")]
[JsonDerivedType(typeof(FirstOp), "first")]
[JsonDerivedType(typeof(SingleOp), "single")]
[JsonDerivedType(typeof(LastOp), "last")]
[JsonDerivedType(typeof(AggregateOp), "aggregate")]
[JsonDerivedType(typeof(PageOp), "page")]
public closed record QueryOp;
// end-snippet

/// <summary>
/// Filters the sequence by a predicate. Written after a <see cref="GroupByOp"/> it filters the groups
/// instead of the rows — SQL <c>HAVING</c> — and its predicate reads the group key and aggregates
/// rather than row members.
/// </summary>
public sealed record WhereOp(Node Predicate) :
    QueryOp;

/// <summary>Orders the sequence by a key. Must be the first ordering operator.</summary>
public sealed record OrderByOp(Node Key, bool Descending) :
    QueryOp;

/// <summary>Adds a secondary ordering. Only valid after an <see cref="OrderByOp"/>.</summary>
public sealed record ThenByOp(Node Key, bool Descending) :
    QueryOp;

/// <summary>Skips a number of elements.</summary>
public sealed record SkipOp(int Count) :
    QueryOp;

/// <summary>Takes at most a number of elements (capped by the server page-size limit).</summary>
public sealed record TakeOp(int Count) :
    QueryOp;

/// <summary>Projects each element to the requested shape.</summary>
public sealed record SelectOp(Projection Projection) :
    QueryOp;

/// <summary>
/// Flattens a collection navigation into a sequence of its elements, which every later operator then
/// reads. <see cref="Path"/> names the collection — reference navigations may precede it, the
/// collection is always the last segment.
/// </summary>
/// <remarks>
/// Unlike a <see cref="SubqueryNode"/>, which folds a collection to a scalar, this replaces the row
/// being queried. The element type is allow-listed in its own right and, because a
/// <c>[QueryableCollection]</c> of a policied type is refused at startup, carries no row policy that
/// the flatten could bypass.
/// </remarks>
public sealed record SelectManyOp(
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path) :
    QueryOp;

/// <summary>
/// Narrows the sequence to the rows of a derived type, which every later operator then reads.
/// <see cref="Type"/> names that type exactly as a request's own root does, so it is resolved —
/// and <b>policy-filtered</b> — through the same allow-list.
/// </summary>
/// <remarks>
/// The name is resolved against the server's schema and checked to derive from the type currently
/// being queried; no CLR type ever comes off the wire. Narrowing composes with the row policies
/// already applied, because a derived type's rows are a subset of the base's.
/// </remarks>
public sealed record OfTypeOp(string Type) :
    QueryOp;

/// <summary>Groups the sequence by one or more keys. A following <see cref="SelectOp"/> may use
/// aggregates and the group key.</summary>
public sealed record GroupByOp(IReadOnlyList<Node> Keys) :
    QueryOp;

/// <summary>
/// Removes duplicate rows. Applied to the projected rows, so it deduplicates the members the query
/// asked for rather than whole entities. Only <c>Skip</c>, <c>Take</c>, the projection itself, and a
/// terminal may follow it.
/// </summary>
public sealed record DistinctOp :
    QueryOp;

/// <summary>
/// Inverts the ordering. Requires an ordered query — reversing an unordered one would invert an order
/// the database never defined.
/// </summary>
public sealed record ReverseOp :
    QueryOp;

/// <summary>
/// Joins a second source to the pipeline. <see cref="Root"/> names that source exactly as a request's
/// own root does, so it is resolved — and <b>policy-filtered</b> — independently before the two sides
/// meet. A join can therefore only ever narrow: no row hidden from a direct query of the inner source
/// is observable through one.
/// </summary>
/// <remarks>
/// The join carries its own projection rather than being followed by a <c>Select</c>, because a
/// projected member has to say which side it reads and an ordinary member path has no room to. That
/// also keeps the joined shape from escaping into later operators, which are all single-rooted.
/// </remarks>
public sealed record JoinOp(
    string Root,
    JoinKind Kind,
    Node OuterKey,
    Node InnerKey,
    Node? InnerPredicate,
    IReadOnlyList<JoinMember> Result) :
    QueryOp
{
    // The wire's constructor: only the members a request has to carry. The inner predicate may be absent, and
    // reaches the reader through its init accessor instead, since an optional parameter would have to
    // trail and the declared order is the one callers write.
    [JsonConstructor]
    public JoinOp(
        string root,
        JoinKind kind,
        Node outerKey,
        Node innerKey,
        IReadOnlyList<JoinMember> result) :
        this(root, kind, outerKey, innerKey, null, result)
    {
    }

    /// <summary>
    /// The inner side's own pipeline, present when it carries more than a predicate: filters, then an
    /// ordering bounded by Skip/Take. Replaces <see cref="InnerPredicate"/> — a request carries one
    /// spelling or the other, never both — and travels under wire version 2, so a server predating it
    /// rejects the request whole rather than reading the inner side partially.
    /// </summary>
    public IReadOnlyList<QueryOp>? InnerOps { get; init; }
}

/// <summary>
/// Combines the pipeline with a second source. <see cref="Root"/> names that source exactly as a
/// request's own root does, so it is resolved — and <b>policy-filtered</b> — independently before the
/// two are combined, the same way a <see cref="JoinOp"/> resolves its second side.
/// </summary>
/// <remarks>
/// Both sides must produce the same shape, so this carries the second side's own projection: its
/// members are matched by name against the pipeline's <c>Select</c>, and the two must agree on the
/// type of each. Only a terminal may follow, because the combined rows have no single root left for a
/// later operator to read.
/// </remarks>
public sealed record SetOp(
    SetKind Kind,
    string Root,
    Node? Predicate,
    Projection Projection) :
    QueryOp
{
    // The wire's constructor: only the members a request has to carry. The predicate may be absent, and
    // reaches the reader through its init accessor instead, since an optional parameter would have to
    // trail and the declared order is the one callers write.
    [JsonConstructor]
    public SetOp(SetKind kind, string root, Projection projection) :
        this(kind, root, null, projection)
    {
    }

    /// <summary>
    /// The operand's own pipeline, present when it carries more than a predicate: filters, then an
    /// ordering bounded by Skip/Take. Replaces <see cref="Predicate"/> — a request carries one
    /// spelling or the other, never both — and travels under wire version 2, so a server predating it
    /// rejects the request whole rather than reading the operand partially.
    /// </summary>
    public IReadOnlyList<QueryOp>? OperandOps { get; init; }
}

/// <summary>
/// Terminal: returns the element count as a scalar, optionally counting only elements matching a
/// predicate.
/// </summary>
public sealed record CountOp(Node? Predicate = null) :
    QueryOp;

/// <summary>
/// Terminal: returns the element count as a 64-bit scalar, optionally counting only elements matching
/// a predicate.
/// </summary>
public sealed record LongCountOp(Node? Predicate = null) :
    QueryOp;

/// <summary>Terminal: returns whether any element matches the optional predicate.</summary>
public sealed record AnyOp(Node? Predicate = null) :
    QueryOp;

/// <summary>Terminal: returns whether every element matches the predicate.</summary>
public sealed record AllOp(Node Predicate) :
    QueryOp;

/// <summary>Terminal: returns the first element (or default) optionally matching a predicate.</summary>
public sealed record FirstOp(bool OrDefault, Node? Predicate = null) :
    QueryOp;

/// <summary>Terminal: returns the single element (or default) optionally matching a predicate.</summary>
public sealed record SingleOp(bool OrDefault, Node? Predicate = null) :
    QueryOp;

/// <summary>
/// Terminal: returns the last element (or default) optionally matching a predicate. Requires an
/// ordered query — the server resolves "last" by reversing the ordering, so an unordered query has
/// no defined last row and is rejected.
/// </summary>
public sealed record LastOp(bool OrDefault, Node? Predicate = null) :
    QueryOp;

/// <summary>
/// Terminal: folds the whole sequence to a single scalar. <see cref="Selector"/> is the member being
/// aggregated. <see cref="AggregateFn.Count"/> is not carried here — it has its own terminal — and
/// <c>Min</c>/<c>Max</c> over an empty sequence return null rather than faulting.
/// </summary>
public sealed record AggregateOp(AggregateFn Function, Node Selector) :
    QueryOp;

/// <summary>
/// Terminal: returns a bounded page of rows plus whether more exist. <see cref="Size"/> is the
/// requested page size; when null the server applies its <c>DefaultPageSize</c>. Either way the
/// effective size is capped by <c>MaxPageSize</c>. <see cref="Cursor"/> is an opaque resume token
/// from a previous page's response; when set the server seeks past it (keyset paging) instead of
/// starting from the beginning. Clients must not parse or synthesize a cursor.
/// </summary>
public sealed record PageOp(int? Size = null, string? Cursor = null) :
    QueryOp;
