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
    // A forgotten scope is kept as an empty one rather than removed, so its generation keeps moving;
    // where the forgetting happened is kept in the value itself (Cleared), written in the same swap
    // that empties it, so there is no instant at which a round can find the scope emptied and not
    // know it was.
    ConcurrentDictionary<(string Policy, string Scope), CachedPolicyScope> scopes = new();

    public CachedPolicyScope? Get(string policy, string scope) =>
        scopes.GetValueOrDefault((policy, scope));

    public void Apply(string policy, string scope, CachedPolicyUpdate update) =>
        scopes.AddOrUpdate(
            (policy, scope),
            // Never read before, so the round can only have been decided against nothing: a
            // generation other than zero names a scope this store has since forgotten it ever held.
            _ =>
            {
                if (update.Generation == 0)
                {
                    return Merge(CachedPolicyScope.Empty, update, resolves: true);
                }

                return CachedPolicyScope.Empty;
            },
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
                if (update.Generation < current.Cleared)
                {
                    return current;
                }

                return Merge(current, update, resolves: false);
            });

    // One swap: the scope is emptied and marked with where that happened together, so a stale round
    // landing at any instant either sees the old scope or sees it forgotten, never a forgotten one
    // it cannot tell from a cold one.
    public void InvalidateScope(string policy, string scope) =>
        scopes.AddOrUpdate(
            (policy, scope),
            _ => CachedPolicyScope.Empty with {Generation = 1, Cleared = 1},
            (_, current) => CachedPolicyScope.Empty with
            {
                Generation = current.Generation + 1,
                Cleared = current.Generation + 1
            });

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
