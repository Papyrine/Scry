/// <summary>
/// One cached row policy as the schema resolved it: what it is called in the store, the host type that
/// makes the decisions, and where the answers live. Built once at startup and shared by every request.
/// </summary>
sealed class CachedPolicyRegistration(Type entity, Type policy, ICachedPolicyStore store, int? maxKeys)
{
    // One gate per scope. Held here rather than on the adapter because the registration is what every
    // request shares; a per-request gate would let every request past it at once.
    readonly ConcurrentDictionary<string, object> gates = new(StringComparer.Ordinal);

    public Type Entity { get; } = entity;

    public Type Policy { get; } = policy;

    public ICachedPolicyStore Store { get; } = store;

    public int? MaxKeys { get; } = maxKeys;

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

    public object Gate(string scope) =>
        gates.GetOrAdd(scope, _ => new());
}
