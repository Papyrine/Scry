namespace Scry.Wire;

/// <summary>Projects each element to the requested shape.</summary>
public sealed record SelectOp(Projection Projection) :
    QueryOp;