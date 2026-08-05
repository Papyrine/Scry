namespace Scry;

/// <summary>
/// Several join keys compared as one: the sides match when every part matches, position by position.
/// Only valid as a <see cref="JoinOp"/> key — a composite has no value of its own, so it can appear
/// nowhere a value can. A server predating this node rejects the request at deserialization rather
/// than joining on less than the whole key.
/// </summary>
public sealed record CompositeKeyNode(IReadOnlyList<Node> Parts) :
    Node;
