/// <summary>
/// Turns an <see cref="ICachedRowPolicy{T}"/> into an ordinary row policy. What the query carries is a
/// membership test against the keys this scope is known to be allowed — cheap, translatable, and
/// composable wherever a policy applies. The expensive decision runs beside the query, only for rows
/// whose answer is not already known.
/// </summary>
/// <remarks>
/// Being an <see cref="IReturnablePolicy{T}"/> is the whole design: the root, a join's inner side, a
/// narrowing, a membership test and a traversal all apply policies through one place, so a cached one
/// reaches every route to a row without any of them knowing it is cached.
/// </remarks>
sealed class CachedRowPolicyAdapter<TEntity, TKey, TVersion>(
    CachedPolicyRegistration registration,
    Expression<Func<TEntity, TKey>> key,
    Expression<Func<TEntity, TVersion>> version) :
    IReturnablePolicy<TEntity>,
    ICachedPolicyAdapter
    where TEntity : class
    where TKey : notnull
    where TVersion : struct
{
    // Compiled once. Read per undecided row, which on a cold scope is every row there is.
    readonly Func<TEntity, TKey> readKey = key.Compile();
    readonly Func<TEntity, TVersion> readVersion = version.Compile();

    /// <summary>
    /// Reached where a policy is applied without the call's own state — the startup probe, which builds
    /// a traversal to prove it translates. Nothing is being read, so nothing is decided.
    /// </summary>
    public IQueryable<TEntity> Filter(IQueryable<TEntity> source, ScryPolicyContext context) =>
        Apply(source, context, new(), refresh: false);

    /// <inheritdoc/>
    public IQueryable Filter(IQueryable source, ScryPolicyContext context, CachedDecisions decisions, bool refresh) =>
        Apply((IQueryable<TEntity>)source, context, decisions, refresh);

    IQueryable<TEntity> Apply(IQueryable<TEntity> source, ScryPolicyContext context, CachedDecisions decisions, bool refresh)
    {
        var allowed = Allowed(context, decisions, refresh);

        // Bound as one parameter rather than written into the statement, so a scope's keys do not
        // become part of the SQL text and one plan serves every caller.
        var predicate = Expression.Lambda<Func<TEntity, bool>>(
            Expression.Call(
                Contains,
                Parameterization.Parameterize(allowed, typeof(TKey[])),
                key.Body),
            key.Parameters[0]);

        return source.Where(predicate);
    }

    TKey[] Allowed(ScryPolicyContext context, CachedDecisions decisions, bool refresh)
    {
        // Once per call however many sites apply this policy: they are all reading the same query's
        // rows, and a set that moved between them would be two answers to one question.
        if (decisions.Get(registration) is TKey[] memo)
        {
            return memo;
        }

        var policy = Resolve(context);
        var scopeKey = policy.ScopeKey(context);
        var scope = refresh
            ? Refresh(policy, scopeKey, context)
            : registration.Store.Get(registration.Name, scopeKey) ?? CachedPolicyScope.Empty;

        var allowed = scope.AllowedKeys.Cast<TKey>().ToArray();
        if (registration.MaxKeys is { } max &&
            allowed.Length > max)
        {
            throw new(
                $"Cached row policy '{registration.Policy.Name}' allows {allowed.Length} rows for one caller, past the configured MaxCachedPolicyKeys of {max}. Every allowed key travels to the database with each query, so a policy that admits an unbounded set is one to write as an ordinary IReturnablePolicy filter instead.");
        }

        decisions.Set(registration, allowed);
        return allowed;
    }

    /// <summary>
    /// Brings a scope up to date and returns it: the rows changed since the last decision, plus the
    /// ones a host threw away, decided and recorded.
    /// </summary>
    /// <remarks>
    /// One caller at a time per scope. A cold scope decides every row, so letting a burst of requests
    /// each do that would multiply the one cost this exists to avoid; the ones that wait find the work
    /// already done.
    /// </remarks>
    CachedPolicyScope Refresh(ICachedRowPolicy<TEntity> policy, string scopeKey, ScryPolicyContext context)
    {
        lock (registration.Gate(scopeKey))
        {
            var current = registration.Store.Get(registration.Name, scopeKey);
            var rows = Undecided(current, context);
            if (rows.Count == 0)
            {
                return current ?? CachedPolicyScope.Empty;
            }

            var watermark = current?.Watermark;
            foreach (var row in rows)
            {
                // The watermark only ever moves forward, so a row decided out of order cannot pull it
                // back and leave the rows between it as already answered.
                var read = readVersion(row);
                if (watermark is not TVersion known ||
                    Comparer<TVersion>.Default.Compare(read, known) > 0)
                {
                    watermark = read;
                }
            }

            // Every key that was pending has now been decided or found gone, so none of them is still
            // waiting for an answer.
            registration.Store.Apply(
                registration.Name,
                scopeKey,
                new(Decide(policy, rows, scopeKey, context), watermark, current?.PendingKeys ?? []));

            return registration.Store.Get(registration.Name, scopeKey) ?? CachedPolicyScope.Empty;
        }
    }

    /// <inheritdoc/>
    public void Prime(string scopeKey, IEnumerable rows, ScryPolicyContext context)
    {
        var primed = rows.Cast<TEntity>().ToList();
        if (primed.Count == 0)
        {
            return;
        }

        var policy = Resolve(context);
        lock (registration.Gate(scopeKey))
        {
            // No watermark and nothing resolved: these are rows the caller chose rather than every row
            // up to a version, so neither claim would be true of anything but them.
            registration.Store.Apply(
                registration.Name,
                scopeKey,
                new(Decide(policy, primed, scopeKey, context), Watermark: null, Resolved: []));
        }
    }

    List<(object, bool)> Decide(ICachedRowPolicy<TEntity> policy, IReadOnlyList<TEntity> rows, string scopeKey, ScryPolicyContext context)
    {
        var decisions = new List<(object, bool)>(rows.Count);
        foreach (var row in rows)
        {
            decisions.Add((readKey(row), policy.Allow(row, scopeKey, context)));
        }

        return decisions;
    }

    ICachedRowPolicy<TEntity> Resolve(ScryPolicyContext context) =>
        (ICachedRowPolicy<TEntity>)(context.Services.GetService(registration.Policy) ??
                                    Activator.CreateInstance(registration.Policy) ??
                                    throw new($"Could not create cached row policy '{registration.Policy.Name}'."));

    /// <summary>
    /// The rows whose answer is not known: those changed past the watermark — every row, where there
    /// is no watermark yet — and those a host invalidated.
    /// </summary>
    /// <remarks>
    /// Read straight off the set rather than through the source's policies: a decision is remembered
    /// per scope and shared, so narrowing the rows it is made over by another policy would bake that
    /// caller's view into an answer other callers go on to read. The other policies still apply to the
    /// query itself, where they belong.
    /// </remarks>
    IReadOnlyList<TEntity> Undecided(CachedPolicyScope? current, ScryPolicyContext context)
    {
        var set = context.Db.Set<TEntity>().AsNoTracking();
        var changed = current?.Watermark is TVersion watermark
            ? set.Where(Newer(watermark))
            : set;

        if (current is not {PendingKeys.Count: > 0})
        {
            return changed.ToList();
        }

        var pending = current.PendingKeys.Cast<TKey>().ToArray();
        var invalidated = set.Where(
            Expression.Lambda<Func<TEntity, bool>>(
                Expression.Call(Contains, Parameterization.Parameterize(pending, typeof(TKey[])), key.Body),
                key.Parameters[0]));

        // A pending row that has since been deleted simply never comes back, which drops its key from
        // the allowed set as surely as a decision against it would have. Deduplicated by key: a row can
        // be both changed and invalidated, and deciding it twice would be work for one answer.
        var rows = changed.ToList();
        var seen = rows.Select(readKey).ToHashSet();
        rows.AddRange(invalidated.ToList().Where(_ => seen.Add(readKey(_))));
        return rows;
    }

    Expression<Func<TEntity, bool>> Newer(TVersion watermark) =>
        Expression.Lambda<Func<TEntity, bool>>(
            Expression.GreaterThan(version.Body, Parameterization.Parameterize(watermark, typeof(TVersion))),
            version.Parameters[0]);

    static readonly MethodInfo Contains = typeof(Enumerable)
        .GetMethods()
        .Single(_ => _.Name == nameof(Enumerable.Contains) && _.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(TKey));
}
