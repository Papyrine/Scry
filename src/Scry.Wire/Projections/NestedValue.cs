namespace Scry.Wire;

/// <summary>A projection member backed by a nested projection into a navigation property.</summary>
public sealed record NestedValue(IReadOnlyList<string> Path, Projection Projection) :
    ProjectionValue;