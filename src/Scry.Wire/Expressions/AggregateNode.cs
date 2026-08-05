namespace Scry;

/// <summary>
/// An aggregate over a group. <see cref="Selector"/> is the member being aggregated (null for
/// <see cref="AggregateFn.Count"/>); <see cref="Separator"/> is carried by
/// <see cref="AggregateFn.Join"/> alone. Only valid inside a projection that follows a group
/// operator.
/// </summary>
public sealed record AggregateNode(AggregateFn Function, Node? Selector, string? Separator = null) :
    Node;