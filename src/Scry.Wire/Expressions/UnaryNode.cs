namespace Scry;

/// <summary>A unary operation over one expression.</summary>
public sealed record UnaryNode(UnaryOp Op, Node Operand) :
    Node;