using System.Text.Json.Serialization;

namespace Scry.Wire;

/// <summary>A <c>Select</c> projection: the named members the result rows should contain.</summary>
public sealed record Projection(IReadOnlyList<ProjectionMember> Members);

/// <summary>One member of a projection. Its value is either an expression or a nested projection
/// (used to project into a related entity via a navigation property).</summary>
public sealed record ProjectionMember(string Name, ProjectionValue Value);

// begin-snippet: wireProjectionValues
/// <summary>The value of a projection member.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ExprValue), "expr")]
[JsonDerivedType(typeof(NestedValue), "nested")]
public abstract record ProjectionValue;
// end-snippet

/// <summary>A projection member backed by an expression (a member path or an aggregate).</summary>
public sealed record ExprValue(Expr Expression) :
    ProjectionValue;

/// <summary>A projection member backed by a nested projection into a navigation property.</summary>
public sealed record NestedValue(IReadOnlyList<string> Path, Projection Projection) :
    ProjectionValue;
