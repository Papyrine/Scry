namespace Scry;

/// <summary>
/// Opts a POCO that is not part of the persisted model into client querying. The server must supply
/// the data at execution time via an in-memory source.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class QueryablePocoAttribute :
    Attribute
{
    /// <summary>
    /// Overrides the source name exposed to clients. Defaults to the type name.
    /// </summary>
    public string? Name { get; set; }
}
