namespace Scry;

/// <summary>
/// How a host tells the server that a cached row policy's answers are out of date, and how it can
/// decide rows ahead of anyone reading them. A version column says a <em>row</em> changed; nothing but
/// this says a <em>grant</em> did.
/// </summary>
/// <remarks>
/// Invalidating is a promise about the future rather than about now: the answer is made again the next
/// time someone reads, and a query already running keeps the answers it started with. That lag is the
/// trade the cache exists to make.
/// </remarks>
public sealed class ScryPolicyCache
{
    readonly Dictionary<Type, CachedPolicyRegistration> registrations;

    internal ScryPolicyCache(IEnumerable<CachedPolicyRegistration> registrations) =>
        this.registrations = registrations.ToDictionary(_ => _.Entity);

    /// <summary>
    /// Forgets everything decided for one caller, so the next query of theirs decides every row again.
    /// What to call when someone's role changed rather than one grant.
    /// </summary>
    public void InvalidateScope<TEntity>(string scopeKey)
    {
        var registration = For<TEntity>();
        registration.Store.InvalidateScope(registration.Name, scopeKey);
    }

    /// <summary>
    /// Forgets what was decided about these rows, for every caller. What to call when a grant on the
    /// rows themselves changed — the version column cannot see that, so nothing else would.
    /// </summary>
    public void InvalidateRows<TEntity>(IReadOnlyCollection<object> keys)
    {
        var registration = For<TEntity>();
        registration.Store.InvalidateRows(registration.Name, keys);
    }

    /// <summary>
    /// Decides these rows for this caller now, so their first read costs nothing. What to call just
    /// after inserting them, where the work would otherwise land on whoever queries next.
    /// </summary>
    /// <remarks>
    /// The watermark does not move: these are rows chosen by the caller rather than every row up to a
    /// version, so moving it would mark rows nobody has decided as already answered. They are decided
    /// once more by the next read that passes their version, which costs one decision and keeps the
    /// watermark meaning exactly one thing.
    /// </remarks>
    public void Prime<TEntity>(string scopeKey, IEnumerable<TEntity> rows, ScryPolicyContext context)
        where TEntity : class =>
        ((ICachedPolicyAdapter)For<TEntity>().Adapter).Prime(scopeKey, rows, context);

    CachedPolicyRegistration For<TEntity>() =>
        registrations.GetValueOrDefault(typeof(TEntity)) ??
        throw new($"'{typeof(TEntity).Name}' carries no cached row policy, so there is nothing cached about it. Register one with options.AddCachedPolicy.");
}

/// <summary>
/// The part of a cached policy's adapter reachable without naming the types it closed over, which is
/// what lets the cache facade hand it rows.
/// </summary>
interface ICachedPolicyAdapter
{
    /// <summary>
    /// Applies the membership test over what this scope is allowed. Called instead of the
    /// <see cref="IReturnablePolicy{T}"/> method a policy is normally applied through, because what
    /// this needs beyond the context — where to memo the answer, and whether it may be brought up to
    /// date — belongs to the call rather than to the policy, and would otherwise have to be hung off
    /// the context every host-written policy also sees.
    /// </summary>
    IQueryable Filter(IQueryable source, ScryPolicyContext context, CachedDecisions decisions, bool refresh);

    void Prime(string scopeKey, IEnumerable rows, ScryPolicyContext context);
}
