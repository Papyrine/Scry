namespace Scry;

/// <summary>
/// A projection member that returns a collection navigation's elements rather than folding them:
/// <c>Titles = _.Divisions.Select(d =&gt; d.Title).ToList()</c>. <see cref="Path"/> names the
/// collection as a <see cref="SubqueryNode"/> does, <see cref="Predicate"/> narrows its elements,
/// and exactly one of <see cref="Selector"/> — one value per element — or <see cref="Projection"/> —
/// a flat object of values per element — says what each element becomes.
/// </summary>
/// <remarks>
/// The one place a response holds a list inside a row. It is bounded on purpose: a member may only
/// be projected at the top level of a projection, its elements may only carry values read off the
/// element, and nothing inside it may be a subquery, a membership test, a nested object or another
/// collection — so a row never carries more than one level of list, and the cost of a request stays
/// one correlated subquery per collection member.
/// </remarks>
public sealed record CollectionValue(
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path,
    Node? Predicate = null,
    Node? Selector = null,
    Projection? Projection = null) :
    ProjectionValue;
