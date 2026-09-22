/// <summary>An allow-listed member of a queryable type.</summary>
sealed class Member
{
    public Member(string name, PropertyInfo property, MemberKind kind) :
        this(name, property, kind, property.PropertyType, command: null)
    {
    }

    Member(string name, PropertyInfo? property, MemberKind kind, Type type, string? command)
    {
        Name = name;
        Property = property;
        Type = type;
        Kind = kind;
        Command = command;
        Element = kind == MemberKind.Collection ? Schema.CollectionElement(type) : null;
        Target = Element ?? Nullable.GetUnderlyingType(type) ?? type;
        BinaryTransfer = property?.HasAttribute<BinaryTransferAttribute>() == true;
        Sensitive = property?.HasAttribute<SensitiveAttribute>() == true;
        ContentType = property?.GetCustomAttribute<AttachmentAttribute>(inherit: false)?.ContentType;
    }

    /// <summary>
    /// The capability a targeted command adds to its target: <c>Can{command}</c>, a <c>bool</c> backed
    /// by no property, which the server computes from the command's policy.
    /// </summary>
    public static Member Capability(string command) =>
        new($"Can{command}", property: null, MemberKind.Capability, typeof(bool), command);

    public string Name { get; }

    /// <summary>The property behind the member. Null for a capability, which nothing stores.</summary>
    public PropertyInfo? Property { get; }

    /// <summary>
    /// The property behind the member, for a reader that only ever reaches one — every kind but a
    /// capability, which it has already branched away from. Throws where a capability reaches here
    /// anyway, rather than building an expression over a property that does not exist.
    /// </summary>
    public PropertyInfo ClrProperty =>
        Property ?? throw new InvalidOperationException($"'{Name}' is a capability, computed rather than stored, and has no property to read.");

    public Type Type { get; }

    public MemberKind Kind { get; }

    /// <summary>The command a capability answers for. Null on every other member.</summary>
    public string? Command { get; }

    /// <summary>
    /// A collection member's element type; null for every other kind. Derived once here rather than
    /// per request, since finding it means walking the declared type's interfaces.
    /// </summary>
    public Type? Element { get; }

    /// <summary>
    /// The type a path continues on after this member: a collection's element, an optional struct
    /// complex member's struct rather than the <c>Nullable&lt;T&gt;</c> it is declared as, and
    /// otherwise the declared type itself. The one unwrap every reader of a path applies.
    /// </summary>
    public Type Target { get; }

    /// <summary>
    /// Whether the member's values travel as raw multipart parts instead of base64 in JSON. A
    /// transfer-encoding concern only — the member is otherwise an ordinary scalar.
    /// </summary>
    public bool BinaryTransfer { get; }

    /// <summary>
    /// Whether the model marks this member <c>[Sensitive]</c>: a query may not compare it against a
    /// constant in a URL, and a response projecting it may not be stored. Read here rather than
    /// re-derived per request, since it never changes for the life of the schema.
    /// </summary>
    /// <remarks>
    /// Read through the override chain, as <c>[QueryIgnore]</c> is: the generator describes an
    /// overridden member with the attributes of every declaration along the chain
    /// (<c>MetadataModelReader.DeclaredProperties</c>), so a base's marking reaches the derived model
    /// there, and reading it declared-only here would let the same member into a URL and a cache the
    /// client refuses it — with the two stamps disagreeing over it. The type-level attribute stays
    /// declared-only on both sides.
    /// </remarks>
    public bool Sensitive { get; }

    /// <summary>
    /// What an <c>[Attachment]</c> member's bytes are, as declared by the attribute: the media type
    /// the fetch is served as, or null for <see cref="AttachmentMedia.Default"/>. Meaningless on any
    /// other kind of member, where the attribute cannot be.
    /// </summary>
    public string? ContentType { get; }
}
