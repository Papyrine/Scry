// begin-snippet: cachedRowPolicy
/// <summary>
/// Scopes <see cref="Order"/> to the regions the caller is granted. Written as a cached policy rather
/// than an ordinary <c>IReturnablePolicy</c> because the decision is a lookup against another system —
/// far too slow to run per row inside every query, and unchanging often enough to be worth remembering.
/// </summary>
public sealed class RegionAccessPolicy(RegionGrants grants) :
    ICachedRowPolicy<Order>
{
    /// <summary>
    /// Which set of answers this call belongs to. The sample has no sign-in, so there is one caller and
    /// one scope, exactly as <c>CacheScope</c> has one; a real app returns the tenant or the principal
    /// resolved from <c>context.Services</c>. Never from a request header — decisions are remembered
    /// per scope, so a caller choosing its own scope key is a caller choosing its own permissions.
    /// </summary>
    public string ScopeKey(ScryPolicyContext context) => "sample";

    /// <summary>
    /// The expensive part. It runs off the query path — for a row that is new, one whose
    /// <see cref="Order.Revision"/> has moved, and every row the first time a scope is read — and never
    /// again just because a query ran.
    /// </summary>
    public bool Allow(Order row, string scopeKey, ScryPolicyContext context) =>
        grants.Allows(scopeKey, row.Region);
}
// end-snippet
