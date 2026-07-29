namespace Scry.Wire;

/// <summary>
/// A question asked about a collection navigation, evaluated by the database as a correlated
/// subquery. <see cref="Path"/> names the collection — reference navigations may precede it, the
/// collection is always the last segment. <see cref="Predicate"/> and <see cref="Selector"/> read the
/// collection's <i>element</i>, not the row the subquery hangs off.
/// </summary>
/// <remarks>
/// The result is always a scalar, so a subquery can appear anywhere a value can and never widens the
/// shape of a response. A subquery may not appear inside another subquery.
/// </remarks>
public sealed record SubqueryNode(
    IReadOnlyList<string> Path,
    SubqueryFn Function,
    Node? Predicate,
    Node? Selector) :
    Node;
