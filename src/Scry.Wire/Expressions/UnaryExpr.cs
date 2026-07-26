namespace Scry.Wire;

/// <summary>A unary operation over one expression.</summary>
public sealed record UnaryExpr(UnaryOp Op, Expr Operand) :
    Expr;