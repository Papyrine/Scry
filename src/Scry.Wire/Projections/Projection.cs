namespace Scry;

/// <summary>A <c>Select</c> projection: the named members the result rows should contain.</summary>
public sealed record Projection(
    [property: JsonConverter(typeof(ProjectionMembersConverter))] IReadOnlyList<ProjectionMember> Members);