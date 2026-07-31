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
}