namespace Scry;

// begin-snippet: wireAggregates
/// <summary>
/// Aggregate functions, used either in a projection over a grouped query or as a terminal folding
/// the whole sequence to one scalar. <see cref="Count"/> is grouped-projection only — counting a
/// sequence has its own terminal — and so is <see cref="Join"/>, which has no terminal form.
/// </summary>
public enum AggregateFn
{
    Count,
    Sum,
    Average,
    Min,
    Max,

    /// <summary>
    /// Joins the group's text values into one string (SQL <c>STRING_AGG</c>), separated by
    /// <see cref="AggregateNode.Separator"/>. The values are ordered by themselves: SQL leaves the
    /// concatenation order unspecified, so the server imposes one, and the same answer reads from
    /// any source.
    /// </summary>
    Join
}
// end-snippet
