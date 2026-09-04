namespace Scry;

/// <summary>
/// An aggregate over a group. <see cref="Selector"/> is the member being aggregated (null for
/// <see cref="AggregateFn.Count"/>); <see cref="Separator"/> is carried by
/// <see cref="AggregateFn.Join"/> alone. Only valid inside a projection that follows a group
/// operator.
/// </summary>
public sealed record AggregateNode(AggregateFn Function, Node? Selector = null, string? Separator = null) :
    Node
{
    /// <summary>
    /// Filters the group's rows before the fold — <c>g.Where(pred).Sum(…)</c>, or the
    /// <c>Count(pred)</c> that abbreviates it. Read against the group's element. Travels under wire
    /// version 2, so a server predating it rejects the request rather than folding unfiltered.
    /// </summary>
    public Node? Predicate { get; init; }

    /// <summary>
    /// Folds only the distinct selected values — <c>g.Select(sel).Distinct().Agg()</c>. Requires a
    /// <see cref="Selector"/>, <see cref="AggregateFn.Count"/> included, which is the one shape Count
    /// carries one in. Travels under wire version 2, like <see cref="Predicate"/>; omitted from the
    /// wire when false, so a version-1 request keeps its exact bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Distinct { get; init; }
}