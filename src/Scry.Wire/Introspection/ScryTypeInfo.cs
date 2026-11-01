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

    /// <summary>
    /// The names of <see cref="Members"/> in the order the model declares them, which is the order a
    /// generated client projects them in when a query writes no <c>Select</c>. <see cref="Members"/> is
    /// sorted by name, so without this a client built from the description would project the same
    /// members in another order and send a different request. Not part of the schema stamp. Null from
    /// a server that does not say, and so absent from the JSON.
    /// </summary>
    public IReadOnlyList<string>? Declared { get; init; }

    /// <summary>
    /// True where the model marks the CLR type itself <c>[Sensitive]</c>, which makes every member read
    /// off it sensitive. Written only when true.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSensitive { get; init; }
}