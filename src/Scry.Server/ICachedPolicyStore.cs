namespace Scry;

/// <summary>
/// Where an <see cref="ICachedRowPolicy{T}"/>'s answers are kept. The shipped implementation holds
/// them in memory (<see cref="MemoryCachedPolicyStore"/>); a deployment that runs more than one server,
/// or wants the answers to survive a restart, registers its own before <c>AddScry</c>.
/// </summary>
/// <remarks>
/// Keys and watermarks travel as <see cref="object"/> so a store can serialize them without knowing
/// the model: they are the CLR values of the key and version members, and a store hands back what it
/// was given.
/// </remarks>
public interface ICachedPolicyStore
{
    /// <summary>
    /// What is known for one policy and scope, or null where nothing is — which is what makes the next
    /// read decide every row rather than assume none is readable.
    /// </summary>
    CachedPolicyScope? Get(string policy, string scope);

    /// <summary>
    /// Records decisions, moves the watermark on, and clears the keys that were re-decided. Must be
    /// atomic per scope: a reader takes the whole state, and one assembled from halves of two updates
    /// would answer with rows that were never true together.
    /// </summary>
    /// <remarks>
    /// The update names the <see cref="CachedPolicyScope.Generation"/> it was decided against, and
    /// deciding takes long enough for the host to speak meanwhile. A store must honour what it said:
    /// where the scope was forgotten since (<see cref="InvalidateScope"/>), the decisions describe
    /// rows the host told it to forget and are dropped whole; where rows were invalidated since
    /// (<see cref="InvalidateRows"/>), the decisions stand but nothing pending is resolved by them,
    /// since the keys the host re-pended are among the ones this round claims to have answered.
    /// Only an update against the current generation resolves what it decided.
    /// </remarks>
    void Apply(string policy, string scope, CachedPolicyUpdate update);

    /// <summary>Forgets a scope, so the next read of it decides every row again.</summary>
    void InvalidateScope(string policy, string scope);

    /// <summary>
    /// Marks rows as needing a fresh decision in every scope. What a host calls when a grant changed
    /// rather than the rows did — the version column cannot see that, so nothing else would.
    /// </summary>
    void InvalidateRows(string policy, IReadOnlyCollection<object> keys);
}

/// <summary>
/// One scope's answers, as one value. Immutable so a request that has read it keeps reading the same
/// rows however the store moves underneath: every key in it was allowed at the same instant.
/// </summary>
/// <param name="AllowedKeys">The keys this scope may see.</param>
/// <param name="Watermark">
/// The highest version decided so far, or null where nothing has been. A row past it is one whose
/// answer is not known yet, which is how a row inserted or changed since is decided on its next read.
/// </param>
/// <param name="PendingKeys">Keys whose answer was thrown away and has not been made again.</param>
public sealed record CachedPolicyScope(
    IReadOnlyCollection<object> AllowedKeys,
    object? Watermark,
    IReadOnlyCollection<object> PendingKeys)
{
    /// <summary>A scope nothing is known about: no keys allowed, no watermark, nothing pending.</summary>
    public static CachedPolicyScope Empty { get; } = new([], null, []);

    /// <summary>
    /// Moves every time the host invalidates something in this scope — rows or the whole of it — and
    /// never otherwise. A round of deciding reads it first and hands it back with its
    /// <see cref="CachedPolicyUpdate"/>, which is how the store tells a round decided before an
    /// invalidation from one decided after. Zero for a scope nothing has ever been said about.
    /// </summary>
    public long Generation { get; init; }
}

/// <summary>What one round of deciding produced.</summary>
/// <param name="Decisions">The rows decided, and what was decided about each.</param>
/// <param name="Watermark">
/// The version to move to, or null to leave it alone — which is what priming does, since it decides
/// rows it chose rather than every row up to a version.
/// </param>
/// <param name="Resolved">Keys that were pending and are no longer, whichever way they were decided.</param>
public sealed record CachedPolicyUpdate(
    IReadOnlyList<(object Key, bool Allowed)> Decisions,
    object? Watermark,
    IReadOnlyCollection<object> Resolved)
{
    /// <summary>
    /// The <see cref="CachedPolicyScope.Generation"/> the decisions were made against — what
    /// <see cref="ICachedPolicyStore.Get"/> returned when the round began, or zero where it returned
    /// nothing. See <see cref="ICachedPolicyStore.Apply"/> for what a store does with a stale one.
    /// </summary>
    public long Generation { get; init; }
}
