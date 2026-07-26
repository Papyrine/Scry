namespace Scry.Wire;

/// <summary>A <c>Select</c> projection: the named members the result rows should contain.</summary>
public sealed record Projection(IReadOnlyList<ProjectionMember> Members);