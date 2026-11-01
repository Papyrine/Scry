/// <summary>
/// One cached row policy as the schema resolved it: what it is called in the store, the host type that
/// makes the decisions, and where the answers live. Built once at startup and shared by every request.
/// </summary>
sealed class CachedPolicyRegistration(Type entity, Type policy, ICachedPolicyStore store, int? maxKeys, int? maxRows, TimeSpan settle)
{
    // One gate per scope. Held here rather than on the adapter because the registration is what every
    // request shares; a per-request gate would let every request past it at once.
    ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    public Type Entity { get; } = entity;

    public Type Policy { get; } = policy;

    public ICachedPolicyStore Store { get; } = store;

    public int? MaxKeys { get; } = maxKeys;

    public int? MaxRows { get; } = maxRows;

    /// <summary>
    /// What this policy's answers are filed under. The policy type's full name: a store outlives the
    /// process, so the name has to be the same one next time rather than an identity of this run's.
    /// </summary>
    public string Name { get; } = policy.FullName ?? policy.Name;

    /// <summary>
    /// The adapter built over this registration. Set once, after construction, because the adapter is
    /// built from the registration and the two are one thing in two pieces.
    /// </summary>
    public object Adapter { get; set; } = null!;

    // How long a version has to have been seen before the watermark may pass it. A writer can take a
    // version and commit after another writer's higher one is read, and a watermark moved straight to
    // the highest version read would never read that row. Waiting this long before passing a version
    // leaves every writer that took a lower one this long to commit.
    public TimeSpan Settle { get; } = settle;

    // Per scope, what the adapter has read above the watermark and not yet passed. Only read and
    // written under the scope's gate.
    ConcurrentDictionary<string, object> settling = new(StringComparer.Ordinal);

    public T Settling<T>(string scope)
        where T : new() =>
        (T) settling.GetOrAdd(scope, _ => new T());

    // A semaphore rather than a monitor so a refresh can wait for the gate without holding a thread.
    public SemaphoreSlim Gate(string scope) =>
        gates.GetOrAdd(scope, _ => new(1, 1));
}
