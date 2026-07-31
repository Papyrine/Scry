namespace Scry;

/// <summary>A projection member backed by an expression (a member path or an aggregate).</summary>
public sealed record NodeValue(Node Node) :
    ProjectionValue;