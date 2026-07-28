namespace Scry;

/// <summary>
/// Names this source, member, or enum value used to be exposed under, which the server keeps
/// accepting from clients that have not been regenerated yet. Purely a server-side compatibility
/// affordance: generated clients only ever use the current name, and previous names are excluded
/// from the schema stamp, so a rename still registers as drift.
/// <para>
/// The names are the previous <em>wire</em> names, not previous CLR names. A CLR type renamed behind
/// a fixed <c>Name</c> never changed its wire name and needs no entry here; changing (or adopting, or
/// dropping) <c>Name</c> does.
/// </para>
/// <para>
/// On an enum value this covers both directions. Requests filtering by the previous name resolve to
/// the current value; results still serialize the current name, but a drifted client's response
/// carries the rename as an alias table, which its reader uses to resolve a value name it does not
/// know back to the one it was generated with.
/// </para>
/// <para>
/// Entries are meant to be pruned once deployed clients have refreshed — keeping them indefinitely
/// accumulates exactly the compatibility debt the stamp is designed to avoid. Once pruned, never reuse
/// a retired name for something else: unknown names fail loudly, but a reused one silently resolves an
/// old client to the wrong source, member, or enum value.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class PreviousNamesAttribute(params string[] names) :
    Attribute
{
    /// <summary>The names still accepted in addition to the current one.</summary>
    public IReadOnlyList<string> Names { get; } = names;
}
