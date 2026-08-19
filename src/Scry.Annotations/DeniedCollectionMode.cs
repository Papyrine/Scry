namespace Scry;

/// <summary>
/// What a <c>[QueryableCollection]</c> of a policied type does — the one position where refusing the
/// member outright is an option, because a collection is aggregated rather than returned row by row.
/// </summary>
public enum DeniedCollectionMode
{
    /// <summary>
    /// Exposing the member is a startup failure. The default: an aggregate over a policied collection
    /// counts what the policy exists to hide unless the subquery is filtered, so the safe answer to a
    /// host that has not chosen is to refuse rather than to guess which of the two it meant.
    /// </summary>
    Refuse,

    /// <summary>
    /// The collection subquery reads through the policy, so denied elements are absent from every
    /// aggregate over it exactly as they are absent from a query of their own source.
    /// </summary>
    Hide,

    /// <summary>
    /// The whole request fails with a permission error where an aggregate would have skipped a denied
    /// element. Discloses existence — see <see cref="DeniedRowMode.Error"/>.
    /// </summary>
    Error
}
