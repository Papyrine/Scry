namespace Scry;

/// <summary>
/// Exposes a collection navigation for aggregation. A collection is invisible without this attribute,
/// exactly like an un-opted-in type, so adding it widens one model's queryable surface and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// An exposed collection is <b>aggregable, not projectable</b>. A client may ask a question about it —
/// <c>Any</c>, <c>All</c>, <c>Count</c>, <c>Sum</c>, <c>Average</c>, <c>Min</c>, <c>Max</c>, evaluated
/// by the database as a correlated subquery — but can never enumerate its rows into a result. Answers
/// are scalars, so the response shape and the page bounds are unchanged: no request can return an
/// unbounded nested collection.
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
