namespace Scry;

/// <summary>
/// One policy in a source's chain, with what its denials produce. Paired rather than carried on the
/// policy type itself because the same policy can be attached to several types, and a host can want a
/// denial to fail one source's queries while quietly narrowing another's.
/// </summary>
public readonly record struct PolicyUse(Type Policy, DeniedRowHandling Handling)
{
    /// <summary>
    /// Whether this policy fails the request for a row it denies read at <paramref name="position"/>,
    /// rather than hiding it.
    /// </summary>
    internal bool Errors(DeniedPosition position) =>
        position switch
        {
            DeniedPosition.RootSingle => Handling.RootSingle == DeniedRowMode.Error,
            DeniedPosition.RootList => Handling.RootList == DeniedRowMode.Error,
            DeniedPosition.Navigation => Handling.Navigation == DeniedRowMode.Error,
            _ => Handling.CollectionNavigation == DeniedCollectionMode.Error
        };
}
