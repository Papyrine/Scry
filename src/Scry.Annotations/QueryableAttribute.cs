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
    /// Overrides the source name exposed to clients. Defaults to the type name.
    /// </summary>
    public string? Name { get; set; }
}
