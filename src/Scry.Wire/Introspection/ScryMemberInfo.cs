namespace Scry;

/// <summary>
/// An allow-listed member. <see cref="TypeDisplay"/> is the exact C# the source generator would emit
/// (e.g. <c>int</c>, <c>string</c>, <c>global::System.DateOnly</c>, <c>Status?</c>,
/// <c>EmployeeQueryModel?</c>) so the explorer can synthesize an identical model.
/// </summary>
/// <remarks>
/// <c>IsCollection</c> marks an aggregable collection navigation. Like a navigation it is not a
/// projection leaf, so it is excluded from the default projection; unlike one it cannot be traversed
/// in a member path.
/// </remarks>
public sealed record ScryMemberInfo(
    string Name,
    string TypeDisplay,
    bool NeedsNullDefault,
    bool IsNavigation,
    bool IsCollection = false)
{
    /// <summary>
    /// The deprecation the model declares with <c>[Obsolete]</c>: null when the member is not
    /// deprecated, otherwise its message, or empty when the attribute carried none. Replicated onto
    /// the synthesized member so a snippet warns exactly where generated client code would.
    /// </summary>
    /// <remarks>
    /// Advisory only, and deliberately outside the schema stamp: an obsolete member is still allowed,
    /// still validated, and still executed, so deprecating one leaves the queryable surface unchanged.
    /// </remarks>
    public string? Obsolete { get; init; }

    /// <summary>
    /// True for an <c>[Attachment]</c> member: one whose value no query reads, fetched instead by its
    /// row's key. <see cref="TypeDisplay"/> already says so — it is the handle type rather than
    /// <c>byte[]</c> — but a reader deciding what to project needs the fact without matching a string.
    /// </summary>
    /// <remarks>
    /// Written only when true, so introspection of a model with no attachment is byte-identical to
    /// what it was before they existed.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsAttachment { get; init; }

    /// <summary>
    /// What an <c>[Attachment]</c> member's bytes are, as the model declared it: the media type the
    /// fetch will be served as. Null where nothing was declared, which is served as
    /// <see cref="AttachmentMedia.Default"/>.
    /// </summary>
    /// <remarks>
    /// Published so tooling can name a download before making the fetch — the explorer offers a link
    /// per attachment and has no other way to know what it is about to receive. Outside the schema
    /// stamp, deliberately: the generated member is a handle either way, so restating what the bytes
    /// are leaves the queryable surface, and every deployed client, exactly as it was.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; init; }

    /// <summary>
    /// True for a member the model marks <c>[Sensitive]</c>: a query comparing it against a constant
    /// must travel in a body rather than a URL, and a response projecting it is never stored.
    /// </summary>
    /// <remarks>
    /// Published so tooling makes the same choice generated code does — the explorer synthesizes its
    /// models from this document and has no other way to know. Written only when true, so introspection
    /// of a model marking nothing is byte-identical to what it was before the attribute existed.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSensitive { get; init; }
}