using System.Collections.Concurrent;

/// <summary>
/// Which regions a caller may see, and the thing <see cref="RegionAccessPolicy"/> consults to find
/// out. Stands in for whatever a real deployment would ask — a permissions service, a rules engine, a
/// graph of groups and delegations — the point being that asking is slow enough that a query cannot
/// afford to do it per row.
/// </summary>
/// <remarks>
/// Nothing here is Scry. It is the sample's own authorization data, kept in memory because the sample
/// has no sign-in and one caller; a real one is a table or another service. What matters for the demo
/// is that changing a grant here changes nothing about an order — so no version column could notice,
/// and the cache has to be told.
/// </remarks>
public sealed class RegionGrants
{
    readonly ConcurrentDictionary<string, HashSet<string>> granted = new(StringComparer.Ordinal);

    int lookups;
    int version;

    /// <summary>
    /// Moves whenever a grant does. Part of the sample's <c>CacheScope</c>, because a caller whose
    /// grants changed is not the same caller as far as a cached response is concerned — and this is
    /// the one thing about them a database change marker can never notice, since none of this is in
    /// the database.
    /// </summary>
    public int Version => Volatile.Read(ref version);

    /// <summary>
    /// How many times the expensive lookup has run. Not something a real one would count — it is here
    /// so the sample can show the cache doing its job, which is otherwise invisible by design.
    /// </summary>
    public int Lookups => Volatile.Read(ref lookups);

    /// <summary>Every region the sample knows about, granted or not.</summary>
    public static IReadOnlyList<string> Regions { get; } = ["North", "South"];

    /// <summary>
    /// Whether this caller may see this region. Deliberately slow: the delay is what a policy written
    /// as an ordinary SQL filter would pay once per row of every query.
    /// </summary>
    public bool Allows(string scope, string region)
    {
        Interlocked.Increment(ref lookups);
        Thread.Sleep(25);
        return For(scope).Contains(region);
    }

    /// <summary>The regions granted to a caller, as a snapshot nothing else can go on to change.</summary>
    public IReadOnlyCollection<string> For(string scope)
    {
        var regions = Granted(scope);
        lock (regions)
        {
            return [.. regions];
        }
    }

    /// <summary>
    /// Grants or revokes a region. The caller is then responsible for telling Scry, which is the whole
    /// point of the demo — see the endpoint in <c>Program.cs</c>.
    /// </summary>
    public void Set(string scope, string region, bool allowed)
    {
        var regions = Granted(scope);
        lock (regions)
        {
            if (allowed)
            {
                regions.Add(region);
            }
            else
            {
                regions.Remove(region);
            }
        }

        Interlocked.Increment(ref version);
    }

    // Every caller starts with everything, so the sample's other pages show the orders they always
    // have and revoking is something this page does rather than something it starts from.
    HashSet<string> Granted(string scope) =>
        granted.GetOrAdd(scope, _ => [.. Regions]);
}
