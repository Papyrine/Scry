namespace Scry;

/// <summary>
/// Membership of a set drawn from another source — SQL <c>IN (SELECT …)</c>. <see cref="Value"/> reads
/// the row being tested; <see cref="Selector"/> and <see cref="Predicate"/> read a row of
/// <see cref="Root"/>, which is a source name exactly as a request's own root is.
/// </summary>
/// <remarks>
/// The named source is resolved and <b>policy-filtered</b> independently before the test, the same way
/// a <see cref="JoinOp"/> resolves its second side. Membership can therefore only ever be of rows the
/// caller could have queried directly: a row the source's policy hides is not in the set, so the test
/// cannot be used to learn that it exists. A membership test may not appear inside another, nor inside
/// a <see cref="SubqueryNode"/>; <see cref="Value"/> alone may carry a subquery, since it reads the
/// row being tested rather than a row of the set.
/// </remarks>
public sealed record InSourceNode(
    Node Value,
    string Root,
    Node Selector,
    Node? Predicate = null) :
    Node;
