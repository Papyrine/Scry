namespace Scry.Wire;

/// <summary>One member of a projection. Its value is either an expression or a nested projection
/// (used to project into a related entity via a navigation property).</summary>
public sealed record ProjectionMember(string Name, ProjectionValue Value);