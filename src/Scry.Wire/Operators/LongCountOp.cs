namespace Scry;

/// <summary>
/// Terminal: returns the element count as a 64-bit scalar, optionally counting only elements matching
/// a predicate.
/// </summary>
public sealed record LongCountOp(Node? Predicate = null) :
    QueryOp;
