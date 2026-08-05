namespace Scry;

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
    /// <summary>
    /// The inner side's own pipeline, present when it carries more than a predicate: filters, then an
    /// ordering bounded by Skip/Take. Replaces <see cref="InnerPredicate"/> — a request carries one
    /// spelling or the other, never both — and travels under wire version 2, so a server predating it
    /// rejects the request whole rather than reading the inner side partially.
    /// </summary>
    public IReadOnlyList<QueryOp>? InnerOps { get; init; }
}

/// <summary>
/// One projected member of a join, naming the side it reads from. <see cref="Path"/> is the member
/// read off that side; for a <see cref="JoinKind.Group"/> join the inner side is a group rather than
/// a row, so its members carry an <see cref="Aggregate"/> and an empty path instead.
/// </summary>
public sealed record JoinMember(string Name, JoinSide Side, IReadOnlyList<string> Path)
{
    /// <summary>Folds the inner group to a scalar. Only valid on the inner side of a group join.</summary>
    public AggregateNode? Aggregate { get; init; }
}
