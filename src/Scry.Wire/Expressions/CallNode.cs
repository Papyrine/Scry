namespace Scry;

/// <summary>A call to one of the closed set of <see cref="KnownFunction"/>s.</summary>
public sealed record CallNode(KnownFunction Function, Node Target, IReadOnlyList<Node> Arguments) :
    Node;