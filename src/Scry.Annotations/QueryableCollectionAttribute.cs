namespace Scry;

/// <summary>
/// Exposes a collection navigation for aggregation and for projection as a list. A collection is
/// invisible without this attribute, exactly like an un-opted-in type, so adding it widens one model's
/// queryable surface and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// An exposed collection is <b>aggregable, and projectable one level deep</b>. A client may ask a
/// question about it — <c>Any</c>, <c>All</c>, <c>Count</c>, <c>Sum</c>, <c>Average</c>, <c>Min</c>,
/// <c>Max</c>, evaluated by the database as a correlated subquery and answered with a scalar — or
/// return its elements as a list inside a projected row, each element one value or one flat object of
/// values. A list never holds a list, a subquery or an object, so a request costs one correlated
/// subquery per projected collection; what a row carries is then bounded by the response budget rather
/// than the page size.
/// </para>
/// <para>
/// The element type must itself be opted in, and must not carry a row policy: a policy filters a
/// source, and a subquery has no source for it to filter, so aggregating over a policied type would
/// count exactly the rows the policy exists to hide. The server refuses to start otherwise.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class QueryableCollectionAttribute :
    Attribute;
