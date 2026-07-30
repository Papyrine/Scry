namespace Scry.Wire;

// begin-snippet: wireSetKinds
/// <summary>How two sequences are combined.</summary>
public enum SetKind
{
    /// <summary>Rows of either side, deduplicated.</summary>
    Union,

    /// <summary>Rows of either side, keeping duplicates.</summary>
    Concat,

    /// <summary>Rows on both sides.</summary>
    Intersect,

    /// <summary>Rows on the first side that are not on the second.</summary>
    Except
}
// end-snippet

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
    QueryOp;
