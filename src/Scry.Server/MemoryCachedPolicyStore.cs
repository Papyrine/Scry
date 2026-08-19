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

    public CachedPolicyScope? Get(string policy, string scope) =>
        scopes.GetValueOrDefault((policy, scope));

    public void Apply(string policy, string scope, CachedPolicyUpdate update) =>
        scopes.AddOrUpdate(
            (policy, scope),
            _ => Merge(CachedPolicyScope.Empty, update),
            (_, current) => Merge(current, update));

    public void InvalidateScope(string policy, string scope) =>
        scopes.TryRemove((policy, scope), out _);

    public void InvalidateRows(string policy, IReadOnlyCollection<object> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        // Every scope of this policy: a grant changed, and which callers that affects is exactly what
        // the policy would have to be run to find out.
        foreach (var key in scopes.Keys.Where(_ => _.Policy == policy))
        {
            scopes.AddOrUpdate(
                key,
                _ => CachedPolicyScope.Empty with {PendingKeys = [.. keys]},
                (_, current) => current with {PendingKeys = [.. current.PendingKeys, .. keys]});
        }
    }

    static CachedPolicyScope Merge(CachedPolicyScope current, CachedPolicyUpdate update)
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

        var pending = current.PendingKeys.Count == 0
            ? current.PendingKeys
            : current.PendingKeys.Where(_ => !update.Resolved.Contains(_)).ToList();

        return new(allowed, update.Watermark ?? current.Watermark, pending);
    }
}
