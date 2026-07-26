namespace Scry.Wire;

/// <summary>
/// An aggregate over a group. <see cref="Selector"/> is the member being aggregated (null for
/// <see cref="AggregateFn.Count"/>). Only valid inside a projection that follows a group operator.
/// </summary>
public sealed record AggregateExpr(AggregateFn Function, Expr? Selector) :
    Expr;