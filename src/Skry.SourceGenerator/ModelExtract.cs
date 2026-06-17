namespace Skry.SourceGenerator;

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
record struct PropertyInfo(string Name, string TypeDisplay, bool NeedsNullDefault);

/// <summary>An enum referenced by a model, re-emitted so the client needs no server reference.</summary>
record struct EnumInfo(string Name, EquatableArray<string> Members);

enum SourceKind
{
    Entity,
    View,
    Poco
}
