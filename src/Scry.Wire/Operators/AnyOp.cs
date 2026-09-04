namespace Scry;

/// <summary>Terminal: returns whether any element matches the optional predicate.</summary>
public sealed record AnyOp(Node? Predicate = null) :
    QueryOp;