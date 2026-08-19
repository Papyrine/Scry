/// <summary>
/// Where a query read a row, which is what decides whether a policy denying it hides the row or fails
/// the request. Internal: the positions are named on the public surface as the four properties of
/// <see cref="DeniedRowHandling"/>, and this is only how the executor asks about one of them.
/// </summary>
enum DeniedPosition
{
    RootSingle,
    RootList,
    Navigation,
    CollectionNavigation
}
