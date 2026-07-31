namespace Scry;

/// <summary>Adds a secondary ordering. Only valid after an <see cref="OrderByOp"/>.</summary>
public sealed record ThenByOp(Node Key, bool Descending) :
    QueryOp;