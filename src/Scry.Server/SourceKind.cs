// Internal, but in Scry rather than the global namespace: the generator has a global SourceKind of its
// own, and Scry.Tests, which sees the internals of both, would find two.
namespace Scry;

/// <summary>
/// The kind of a queryable source, used by the server registry to decide how to resolve it.
/// </summary>
enum SourceKind
{
    /// <summary>A table-backed EF Core entity.</summary>
    Entity,

    /// <summary>A keyless EF Core entity mapped to a database view.</summary>
    View,

    /// <summary>A POCO that is not part of the persisted model, supplied at execution time.</summary>
    Poco
}
