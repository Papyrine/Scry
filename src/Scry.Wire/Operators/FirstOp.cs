namespace Scry;

/// <summary>Terminal: returns the first element (or default) optionally matching a predicate.</summary>
public sealed record FirstOp(bool OrDefault, Node? Predicate) :
    QueryOp;