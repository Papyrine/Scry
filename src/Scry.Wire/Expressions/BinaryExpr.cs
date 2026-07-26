namespace Scry.Wire;

/// <summary>A binary operation over two expressions.</summary>
public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) :
    Expr;