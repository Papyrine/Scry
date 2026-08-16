namespace Scry;

/// <summary>A projection member backed by a nested projection into a navigation property.</summary>
public sealed record NestedValue(
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path,
    Projection Projection) :
    ProjectionValue;