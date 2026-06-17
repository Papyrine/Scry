namespace Pneumatic;

/// <summary>
/// Opts a keyless EF Core entity (mapped to a database view) into client querying.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class QueryableViewAttribute :
    Attribute
{
    /// <summary>
    /// Overrides the source name exposed to clients. Defaults to the type name.
    /// </summary>
    public string? Name { get; set; }
}
