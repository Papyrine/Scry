namespace Scry.Wire;

/// <summary>Filters the sequence by a predicate.</summary>
public sealed record WhereOp(Node Predicate) :
    QueryOp;