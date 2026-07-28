namespace Scry;

/// <summary>
/// Opts a table-backed EF Core entity into client querying. Without this attribute a type is never
/// exposed (default-deny). All public readable properties are exposed unless marked
/// <see cref="QueryIgnoreAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class QueryableAttribute :
    Attribute
{
    /// <summary>
    /// Overrides the source name exposed to clients — the <c>root</c> of a wire request and the
    /// property emitted on the generated query entry point. Defaults to the type name, so setting it
    /// lets the server type be renamed without breaking deployed clients. Blank is treated as unset.
    /// <para>
    /// Changing this value (or adopting or dropping it) renames the source on the wire. Pair it with
    /// <see cref="PreviousNamesAttribute"/> so deployed clients keep working while they refresh.
    /// </para>
    /// </summary>
    public string? Name { get; set; }
}
