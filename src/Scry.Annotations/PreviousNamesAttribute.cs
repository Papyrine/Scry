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
/// On an enum value this covers the request direction only. A renamed value appearing in a
/// <em>result</em> is serialized under its current name — one value cannot be written under two names
/// — so a client generated before the rename can filter by the old name but cannot deserialize a row
/// carrying it.
/// </para>
/// <para>
/// Entries are meant to be pruned once deployed clients have refreshed — keeping them indefinitely
/// accumulates exactly the compatibility debt the stamp is designed to avoid.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class PreviousNamesAttribute(params string[] names) :
    Attribute
{
    /// <summary>The names still accepted in addition to the current one.</summary>
    public IReadOnlyList<string> Names { get; } = names;
}
