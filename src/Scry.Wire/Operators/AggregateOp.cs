namespace Scry;

/// <summary>
/// Terminal: folds the whole sequence to a single scalar. <see cref="Selector"/> is the member being
/// aggregated. <see cref="AggregateFn.Count"/> is not carried here — it has its own terminal — and
/// <c>Min</c>/<c>Max</c> over an empty sequence return null rather than faulting.
/// </summary>
public sealed record AggregateOp(AggregateFn Function, Node Selector) :
    QueryOp;
