/// <summary>An allow-listed member of a queryable type.</summary>
sealed class Member(string name, PropertyInfo property, MemberKind kind)
{
    public string Name { get; } = name;
    public PropertyInfo Property { get; } = property;
    public Type Type { get; } = property.PropertyType;
    public MemberKind Kind { get; } = kind;

    /// <summary>
    /// A collection member's element type; null for every other kind. Derived once here rather than
    /// per request, since finding it means walking the declared type's interfaces.
    /// </summary>
    public Type? Element { get; } = CollectionElement(kind, property);

    /// <summary>
    /// The type a path continues on after this member: a collection's element, an optional struct
    /// complex member's struct rather than the <c>Nullable&lt;T&gt;</c> it is declared as, and
    /// otherwise the declared type itself. The one unwrap every reader of a path applies.
    /// </summary>
    public Type Target { get; } =
        CollectionElement(kind, property) ??
        Nullable.GetUnderlyingType(property.PropertyType) ??
        property.PropertyType;

    static Type? CollectionElement(MemberKind kind, PropertyInfo property)
    {
        if (kind == MemberKind.Collection)
        {
            return Schema.CollectionElement(property.PropertyType);
        }

        return null;
    }

    /// <summary>
    /// Whether the member's values travel as raw multipart parts instead of base64 in JSON. A
    /// transfer-encoding concern only — the member is otherwise an ordinary scalar.
    /// </summary>
    public bool BinaryTransfer { get; } = property.HasAttribute<BinaryTransferAttribute>();

    /// <summary>
    /// Whether the model marks this member <c>[Sensitive]</c>: a query may not compare it against a
    /// constant in a URL, and a response projecting it may not be stored. Read here rather than
    /// re-derived per request, since it never changes for the life of the schema.
    /// </summary>
    /// <remarks>
    /// Declared-only, matching every other opt-in read: the metadata the generator reads carries a
    /// type's own attributes and nothing inherited, and the two sides have to agree.
    /// </remarks>
    public bool Sensitive { get; } = property.HasAttribute<SensitiveAttribute>(inherit: false);

    /// <summary>
    /// What an <c>[Attachment]</c> member's bytes are, as declared by the attribute: the media type
    /// the fetch is served as, or null for <see cref="AttachmentMedia.Default"/>. Meaningless on any
    /// other kind of member, where the attribute cannot be.
    /// </summary>
    public string? ContentType { get; } =
        property.GetCustomAttribute<AttachmentAttribute>(inherit: false)?.ContentType;
}
