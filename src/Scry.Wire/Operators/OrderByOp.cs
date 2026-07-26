namespace Scry.Wire;

/// <summary>Orders the sequence by a key. Must be the first ordering operator.</summary>
public sealed record OrderByOp(Node Key, bool Descending) :
    QueryOp;