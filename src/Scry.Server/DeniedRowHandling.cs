namespace Scry;

/// <summary>
/// What one policy's denied rows produce, per position a row can be read from. A denial means
/// something different depending on where it lands — a missing list element, a single-row result that
/// found nothing, a navigation reading null, an aggregate counting one fewer — so each is answered
/// separately rather than by one setting standing for all four.
/// </summary>
/// <remarks>
/// Every default is the non-disclosing one, which is what the server did before this existed: hide the
/// row, and refuse a collection of a policied type outright. Raising any of them to
/// <see cref="DeniedRowMode.Error"/> is a deliberate trade of secrecy for a clear answer — see
/// <c>docs/security.md</c>.
/// </remarks>
public sealed record DeniedRowHandling
{
    /// <summary>Where the query returns a single row (<c>First</c>, <c>Single</c>, <c>Last</c>).</summary>
    public DeniedRowMode RootSingle { get; init; }

    /// <summary>
    /// Where the query returns rows — a list, page, or stream — or folds them into a count or
    /// aggregate.
    /// </summary>
    public DeniedRowMode RootList { get; init; }

    /// <summary>Where a navigation steps into the policied source.</summary>
    public DeniedRowMode Navigation { get; init; }

    /// <summary>Where a <c>[QueryableCollection]</c> aggregates the policied type.</summary>
    public DeniedCollectionMode CollectionNavigation { get; init; }

    /// <summary>Hide everywhere, refuse collections: what a policy carries unless a host says otherwise.</summary>
    public static DeniedRowHandling Default { get; } = new();
}
