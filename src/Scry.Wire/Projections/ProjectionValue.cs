namespace Scry.Wire;

// begin-snippet: wireProjectionValues
/// <summary>The value of a projection member.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(NodeValue), "expr")]
[JsonDerivedType(typeof(NestedValue), "nested")]
public abstract record ProjectionValue;
// end-snippet
