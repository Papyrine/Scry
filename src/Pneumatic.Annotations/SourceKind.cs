namespace Pneumatic;

/// <summary>
/// The kind of a queryable source, used by the server registry to decide how to resolve it.
/// </summary>
public enum SourceKind
{
    /// <summary>A table-backed EF Core entity.</summary>
    Entity,

    /// <summary>A keyless EF Core entity mapped to a database view.</summary>
    View,

    /// <summary>A POCO that is not part of the persisted model, supplied at execution time.</summary>
    Poco
}
