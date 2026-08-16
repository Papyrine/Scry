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
/// <remarks>
/// <c>Properties</c> holds the members the type declares itself. <c>BaseModelName</c> is the generated
/// model this one derives from, when the CLR type derives from another opted-in type: the emitted
/// model inherits it rather than repeating its members, which is what makes <c>OfType</c> expressible
/// in the generated surface. Null where there is no opted-in base.
/// </remarks>
/// <param name="ClrName">
/// The model type's own simple CLR name, which the source name may have overridden. Read only to
/// derive a key by EF's <c>{TypeName}Id</c> convention, where the type's name is what counts.
/// </param>
/// <param name="Keys">
/// The members the row's primary key is derived to be, ordinal by name — the order attachment keys
/// travel in, since a composite key's declared order is not visible here. Empty unless the model
/// carries an attachment: nothing else needs a key, and populating it everywhere would move every
/// stamp.
/// </param>
record struct SourceInfo(
    string SourceName,
    string ModelName,
    SourceKind Kind,
    EquatableArray<PropertyInfo> Properties,
    string? BaseModelName = null,
    string? Obsolete = null,
    string ClrName = "",
    EquatableArray<string> Keys = default,
    bool IsSensitive = false);

/// <summary>An allow-listed property and the C# type the client DTO should expose.</summary>
/// <param name="IsNavigation">
/// True for a reference navigation to another query model. Excluded from the default projection the
/// entry point emits, which lists scalars only — matching the server's own default projection.
/// </param>
/// <param name="IsCollection">
/// True for an aggregable collection navigation. Excluded from the default projection for the same
/// reason as a navigation: it is not a scalar leaf, and its rows are never returned.
/// </param>
/// <param name="Obsolete">
/// Null when the model member is not <c>[Obsolete]</c>; otherwise its deprecation message, or empty
/// when the attribute carried none. Replicated onto the generated member so a client sees the
/// deprecation it would otherwise never learn about — it never references the model assembly.
/// </param>
/// <param name="IsAttachment">
/// True for a <c>[Attachment]</c> member. <c>TypeDisplay</c> still holds the member's own type, which
/// is what says whether the attribute was applied to something that can carry one; the emitted type is
/// <c>ScryAttachment</c> either way, so the two are read together.
/// </param>
/// <param name="HasBinaryTransfer">
/// True for a <c>[BinaryTransfer]</c> member. Read only to refuse it alongside
/// <paramref name="IsAttachment"/> — on its own the attribute changes nothing the generator emits.
/// </param>
/// <param name="IsKey">True for a <c>[Key]</c> member, which takes precedence over the name conventions.</param>
/// <param name="IsSensitive">
/// True for a <c>[Sensitive]</c> member: one a query may not compare against a constant in a URL, and
/// may not have projected into a cacheable response. Re-emitted as <c>[ScrySensitive]</c> so the client
/// can make the first of those choices before it sends anything.
/// </param>
record struct PropertyInfo(
    string Name,
    string TypeDisplay,
    bool NeedsNullDefault,
    bool IsNavigation = false,
    bool IsCollection = false,
    string? Obsolete = null,
    bool IsAttachment = false,
    bool HasBinaryTransfer = false,
    bool IsKey = false,
    bool IsSensitive = false);

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
