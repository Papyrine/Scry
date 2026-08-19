namespace Scry;

/// <summary>
/// One policy in a source's chain, with what its denials produce. Paired rather than carried on the
/// policy type itself because the same policy can be attached to several types, and a host can want a
/// denial to fail one source's queries while quietly narrowing another's.
/// </summary>
public readonly record struct PolicyUse(Type Policy, DeniedRowHandling Handling)
{
    /// <summary>
    /// The policy object to use, where the schema built one rather than leaving it to be resolved per
    /// request. A cached policy's adapter holds the store and the compiled accessors its whole
    /// deployment shares, so it is built once at startup and is the same object every request applies.
    /// </summary>
    internal object? Instance { get; init; }


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
