namespace Scry;

/// <summary>
/// Terminal: returns the element count as a scalar, optionally counting only elements matching a
/// predicate.
/// </summary>
public sealed record CountOp(Node? Predicate = null) :
    QueryOp;
