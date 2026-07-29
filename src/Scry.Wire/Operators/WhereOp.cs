namespace Scry.Wire;

/// <summary>
/// Filters the sequence by a predicate. Written after a <see cref="GroupByOp"/> it filters the groups
/// instead of the rows — SQL <c>HAVING</c> — and its predicate reads the group key and aggregates
/// rather than row members.
/// </summary>
public sealed record WhereOp(Node Predicate) :
    QueryOp;
