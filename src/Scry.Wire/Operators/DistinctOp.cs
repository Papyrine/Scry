namespace Scry;

/// <summary>
/// Removes duplicate rows. Applied to the projected rows, so it deduplicates the members the query
/// asked for rather than whole entities. Only <c>Skip</c>, <c>Take</c>, the projection itself, and a
/// terminal may follow it.
/// </summary>
public sealed record DistinctOp :
    QueryOp;
