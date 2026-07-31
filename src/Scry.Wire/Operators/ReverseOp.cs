namespace Scry;

/// <summary>
/// Inverts the ordering. Requires an ordered query — reversing an unordered one would invert an order
/// the database never defined.
/// </summary>
public sealed record ReverseOp :
    QueryOp;
