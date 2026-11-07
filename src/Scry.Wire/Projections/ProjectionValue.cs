namespace Scry;

// begin-snippet: wireProjectionValues
/// <summary>
/// The value of a projection member. The set is closed, so a projection member is an expression, a
/// nested projection or a projected collection, and can be nothing else.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(NodeValue), "node")]
[JsonDerivedType(typeof(NestedValue), "nested")]
[JsonDerivedType(typeof(CollectionValue), "collection")]
public closed record ProjectionValue;

/// <summary>A projection member backed by an expression (a member path or an aggregate).</summary>
public sealed record NodeValue(Node Node) :
    ProjectionValue;

/// <summary>A projection member backed by a nested projection into a navigation property.</summary>
public sealed record NestedValue(
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path,
    Projection Projection) :
    ProjectionValue;
// end-snippet
