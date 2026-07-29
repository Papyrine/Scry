namespace Scry.Wire;

/// <summary>
/// Terminal: returns the last element (or default) optionally matching a predicate. Requires an
/// ordered query — the server resolves "last" by reversing the ordering, so an unordered query has
/// no defined last row and is rejected.
/// </summary>
public sealed record LastOp(bool OrDefault, Node? Predicate) :
    QueryOp;
