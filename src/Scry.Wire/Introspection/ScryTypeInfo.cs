namespace Scry;

/// <summary>
/// A generated client query-model type and its allow-listed members. <see cref="Members"/> lists the
/// members the type declares itself; when it has a <see cref="Base"/>, the base's members are its own
/// and the generated model inherits them.
/// </summary>
public sealed record ScryTypeInfo(string Model, IReadOnlyList<ScryMemberInfo> Members)
{
    /// <summary>
    /// The generated model this one derives from, when its CLR type derives from another allow-listed
    /// type. Null for a type with no allow-listed base — which is every type in a model without an
    /// opted-in hierarchy.
    /// </summary>
    public string? Base { get; init; }

    /// <summary>
    /// The deprecation the model declares on the CLR type with <c>[Obsolete]</c>, in the same form as
    /// <see cref="ScryMemberInfo.Obsolete"/>: null when absent, otherwise the message, or empty when
    /// the attribute carried none.
    /// </summary>
    public string? Obsolete { get; init; }

    /// <summary>
    /// The members forming the row's primary key, ordinal by name. Set only on a type carrying an
    /// <see cref="ScryMemberInfo.IsAttachment"/> member, which is the only thing fetched by key; null
    /// everywhere else, and so absent from the JSON.
    /// </summary>
    public IReadOnlyList<string>? Keys { get; init; }
}