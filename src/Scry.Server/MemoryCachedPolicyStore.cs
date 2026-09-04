namespace Scry;

/// <summary>
/// The default <see cref="ICachedPolicyStore"/>: answers held in this process, for as long as it runs.
/// </summary>
/// <remarks>
/// Each scope is one immutable value replaced whole, so a reader never sees a half-applied update and
/// nothing has to be copied to read it. What this does not do is share anything: every server warms
/// its own answers, and a restart decides every row again. A deployment where that costs too much
/// registers a store of its own.
/// </remarks>
public sealed class MemoryCachedPolicyStore :
    ICachedPolicyStore
{
    readonly ConcurrentDictionary<(string Policy, string Scope), CachedPolicyScope> scopes = new();

    // The generation each scope was last forgotten at. A forgotten scope is kept as an empty one
    // rather than removed, so its generation keeps moving; this remembers where the forgetting
    // happened, which is what tells a round decided before it from one decided after.
    readonly ConcurrentDictionary<(string Policy, string Scope), long> cleared = new();

    public CachedPolicyScope? Get(string policy, string scope) =>
        scopes.GetValueOrDefault((policy, scope));

    public void Apply(string policy, string scope, CachedPolicyUpdate update)
    {
        var key = (policy, scope);
        scopes.AddOrUpdate(
            key,
            // Never read before, so the round can only have been decided against nothing: a
            // generation other than zero names a scope this store has since forgotten it ever held.
            _ => update.Generation == 0 ? Merge(CachedPolicyScope.Empty, update, resolves: true) : CachedPolicyScope.Empty,
            (_, current) =>
            {
                if (update.Generation == current.Generation)
                {
                    return Merge(current, update, resolves: true);
                }

                // The host spoke while the round was deciding. Forgotten since, the decisions
                // describe rows it said to forget and are dropped whole; rows invalidated since, the
                // decisions stand but resolve nothing, since what the host re-pended is among what
                // this round claims to have answered. The next round decides those again.
                if (cleared.TryGetValue(key, out var clearedAt) &&
                    update.Generation < clearedAt)
                {
                    return current;
                }

                return Merge(current, update, resolves: false);
            });
    }

    public void InvalidateScope(string policy, string scope)
    {
        var key = (policy, scope);
        var forgotten = scopes.AddOrUpdate(
            key,
            _ => CachedPolicyScope.Empty with {Generation = 1},
            (_, current) => CachedPolicyScope.Empty with {Generation = current.Generation + 1});
        cleared[key] = forgotten.Generation;
    }

    public void InvalidateRows(string policy, IReadOnlyCollection<object> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        // Every scope of this policy: a grant changed, and which callers that affects is exactly what
        // the policy would have to be run to find out. A key already pending stays pending once.
        foreach (var key in scopes.Keys.Where(_ => _.Policy == policy))
        {
            scopes.AddOrUpdate(
                key,
                _ => CachedPolicyScope.Empty with {PendingKeys = new HashSet<object>(keys), Generation = 1},
                (_, current) => current with
                {
                    PendingKeys = Union(current.PendingKeys, keys),
                    Generation = current.Generation + 1
                });
        }
    }

    static CachedPolicyScope Merge(CachedPolicyScope current, CachedPolicyUpdate update, bool resolves)
    {
        var allowed = new HashSet<object>(current.AllowedKeys);
        foreach (var (key, allow) in update.Decisions)
        {
            // A row decided against is removed rather than left: the same row can have been allowed by
            // an earlier decision, and this one is the current answer.
            if (allow)
            {
                allowed.Add(key);
            }
            else
            {
                allowed.Remove(key);
            }
        }

        var pending = resolves && current.PendingKeys.Count > 0 && update.Resolved.Count > 0
            ? Except(current.PendingKeys, update.Resolved)
            : current.PendingKeys;

        return current with
        {
            AllowedKeys = allowed,
            Watermark = update.Watermark ?? current.Watermark,
            PendingKeys = pending
        };
    }

    static HashSet<object> Union(IReadOnlyCollection<object> first, IReadOnlyCollection<object> second)
    {
        var union = new HashSet<object>(first);
        union.UnionWith(second);
        return union;
    }

    static HashSet<object> Except(IReadOnlyCollection<object> keys, IReadOnlyCollection<object> removed)
    {
        var remaining = new HashSet<object>(keys);
        remaining.ExceptWith(removed);
        return remaining;
    }
}
