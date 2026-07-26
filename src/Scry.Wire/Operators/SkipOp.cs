namespace Scry.Wire;

/// <summary>Skips a number of elements.</summary>
public sealed record SkipOp(int Count) :
    QueryOp;