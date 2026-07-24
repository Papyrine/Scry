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
    /// </summary>
    public string? Name { get; set; }
}
