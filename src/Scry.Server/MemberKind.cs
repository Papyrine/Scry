/// <summary>How a member of a queryable type may be used.</summary>
enum MemberKind
{
    /// <summary>A scalar/enum/string value — usable in predicates, ordering and projection leaves.</summary>
    Scalar,

    /// <summary>A reference navigation to another queryable type — traversable in a member path.</summary>
    Navigation,

    /// <summary>
    /// A collection navigation opted in with <c>[QueryableCollection]</c>. Aggregable but neither
    /// traversable nor projectable: it is the target of a subquery, never a step in a member path or a
    /// projection leaf.
    /// </summary>
    Collection,

    /// <summary>
    /// A <c>byte[]</c> marked <c>[Attachment]</c>. Not addressable by a query at all — not comparable,
    /// orderable, traversable, or projectable — because no query ever reads its value: it is fetched
    /// by its row's key through the attachment endpoint instead.
    /// </summary>
    Attachment,

    /// <summary>
    /// The <c>bool</c> a targeted command adds to its target: whether the caller may send it against
    /// the row. Read like a scalar — compared, ordered, projected — but backed by no property: the
    /// server computes it from the command's policy, in the query, and it is never traversed.
    /// </summary>
    Capability
}
