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
    /// Overrides the source name exposed to clients — the <c>root</c> of a wire request and the
    /// property emitted on the generated query entry point. Defaults to the type name. Blank is
    /// treated as unset.
    /// </summary>
    public string? Name { get; set; }
}
