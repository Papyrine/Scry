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
/// The element type must itself be opted in. A row policy it carries filters the collection's
/// subquery as it filters the element's own source, so an aggregate counts, and a projected
/// collection returns, only the rows the policy allows — unless the policy's
/// <c>CollectionNavigation</c> says to refuse the collection, which the server then does at startup.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class QueryableCollectionAttribute :
    Attribute;
