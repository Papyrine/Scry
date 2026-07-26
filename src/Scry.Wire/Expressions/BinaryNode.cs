namespace Scry.Wire;

/// <summary>A binary operation over two expressions.</summary>
public sealed record BinaryNode(BinaryOp Op, Node Left, Node Right) :
    Node;