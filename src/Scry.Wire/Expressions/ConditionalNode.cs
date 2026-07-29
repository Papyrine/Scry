namespace Scry.Wire;

/// <summary>A conditional expression (<c>test ? ifTrue : ifFalse</c>).</summary>
public sealed record ConditionalNode(Node Test, Node IfTrue, Node IfFalse) :
    Node;
