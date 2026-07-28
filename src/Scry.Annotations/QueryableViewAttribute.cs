namespace Scry;

/// <summary>
/// Opts a keyless EF Core entity (mapped to a database view) into client querying.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class QueryableViewAttribute :
    Attribute
{
    /// <summary>
    /// Overrides the source name exposed to clients — the <c>root</c> of a wire request and the
    /// property emitted on the generated query entry point. Defaults to the type name. Blank is
    /// treated as unset.
    /// <para>
    /// Changing this value (or adopting or dropping it) renames the source on the wire. Pair it with
    /// <see cref="PreviousNamesAttribute"/> so deployed clients keep working while they refresh.
    /// </para>
    /// </summary>
    public string? Name { get; set; }
}
