/// <summary>
/// The allow-listed model surface extracted from the server model assembly. Structural equality
/// (via <see cref="EquatableArray{T}"/>) lets the incremental pipeline skip regeneration when an
/// unrelated change to the DLL leaves the queryable surface unchanged.
/// </summary>
record struct ModelExtract(
    string? Error,
    EquatableArray<SourceInfo> Sources,
    EquatableArray<EnumInfo> Enums)
{
    public static readonly ModelExtract Empty = new(null, new([]), new([]));
}

/// <summary>A queryable source: its wire name, the generated model name, and its members.</summary>
record struct SourceInfo(
    string SourceName,
    string ModelName,
    SourceKind Kind,
    EquatableArray<PropertyInfo> Properties);

/// <summary>An allow-listed property and the C# type the client DTO should expose.</summary>
/// <param name="IsNavigation">
/// True for a reference navigation to another query model. Excluded from the default projection the
/// entry point emits, which lists scalars only — matching the server's own default projection.
/// </param>
/// <param name="IsCollection">
/// True for an aggregable collection navigation. Excluded from the default projection for the same
/// reason as a navigation: it is not a scalar leaf, and its rows are never returned.
/// </param>
record struct PropertyInfo(
    string Name,
    string TypeDisplay,
    bool NeedsNullDefault,
    bool IsNavigation = false,
    bool IsCollection = false);

/// <summary>An enum referenced by a model, re-emitted so the client needs no server reference.</summary>
record struct EnumInfo(string Name, EquatableArray<string> Members);

enum SourceKind
{
    Entity,
    View,
    Poco,

    // A complex value type: a QueryModel is generated and it is a valid navigation target, but it is
    // not a root source, so no entry point is emitted on the ScryQuery facade.
    Complex
}
