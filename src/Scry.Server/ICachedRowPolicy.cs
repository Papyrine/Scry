namespace Scry;

/// <summary>
/// A row policy whose decision is too expensive to make in SQL. Rather than filtering a query, it
/// answers one row at a time in C#; the server remembers the answers and composes the cheap filter —
/// "the keys this caller may see" — everywhere a row policy applies.
/// </summary>
/// <remarks>
/// <para>
/// The decision is made for a row when it is new or has changed since the last one was made for it
/// (see <c>ScryOptions.AddCachedPolicy</c>, which names the column that says so), when the host has
/// invalidated it, and once for every row when a scope is first read. It is never made again just
/// because a query ran.
/// </para>
/// <para>
/// That is the trade: a permission change reaches queries only once the host says it has, so the
/// answers can lag — while a row that is new or has changed is decided on its first read, so a row
/// nobody has ruled on is never assumed to be readable.
/// </para>
/// </remarks>
public interface ICachedRowPolicy<in T>
{
    /// <summary>
    /// Which set of answers this call belongs to: who is asking. Decisions are remembered per scope
    /// and shared by every request that names the same one, so this must identify the caller's
    /// authority and nothing else — two callers given the same scope key see the same rows.
    /// </summary>
    /// <remarks>
    /// Resolve it from <see cref="ScryPolicyContext.Services"/>, which is where the authenticated
    /// principal is. Never from <see cref="ScryPolicyContext.RequestHeaders"/>: a caller choosing its
    /// own scope key is a caller choosing its own permissions.
    /// </remarks>
    string ScopeKey(ScryPolicyContext context);

    /// <summary>
    /// Whether this scope may see this row. The expensive call, and the reason for the rest: it runs
    /// off the query path, for the rows whose answer is not already known.
    /// </summary>
    bool Allow(T row, string scopeKey, ScryPolicyContext context);
}
