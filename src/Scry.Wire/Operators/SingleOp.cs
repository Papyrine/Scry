namespace Scry;

/// <summary>Terminal: returns the single element (or default) optionally matching a predicate.</summary>
public sealed record SingleOp(bool OrDefault, Node? Predicate = null) :
    QueryOp;