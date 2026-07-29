namespace Scry.Wire;

/// <summary>Terminal: returns whether every element matches the predicate.</summary>
public sealed record AllOp(Node Predicate) :
    QueryOp;
