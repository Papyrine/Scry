namespace Scry;

/// <summary>
/// What a <c>[QueryableCollection]</c> of a policied type does — the one position where refusing the
/// member outright is an option, because a collection is aggregated rather than returned row by row.
/// </summary>
public enum DeniedCollectionMode
{
    /// <summary>
    /// Exposing the member is a startup failure, naming the collection and the fix. For a host that
    /// would rather a policied collection stayed unexposed than be read through its policy without
    /// anyone having said so.
    /// </summary>
    Refuse,

    /// <summary>
    /// The collection subquery reads through the policy, so denied elements are absent from every
    /// aggregate over it exactly as they are absent from a query of their own source. The default: it
    /// is the one answer that discloses nothing, and the one a policy already gives everywhere else.
    /// </summary>
    Hide,

    /// <summary>
    /// The whole request fails with a permission error where an aggregate would have skipped a denied
    /// element. Discloses existence — see <see cref="DeniedRowMode.Error"/>.
    /// </summary>
    Error
}
