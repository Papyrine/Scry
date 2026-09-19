namespace Scry;

/// <summary>
/// Carries <see cref="ScryChange"/>s between the nodes of a deployment, so a write on one re-asks the
/// live queries held by the others. Implemented over whatever the deployment already runs — Redis, a
/// service bus, an in-process broker — and chosen with <c>ScryOptions.UseBackplane</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two methods because a publisher and a subscriber are different things to be: a node with no live
/// query of its own still has to say what it wrote, and has no reason to listen. Subscribing is
/// asynchronous and answers with something asynchronously disposable, since both ends of it are a
/// round trip on every real transport.
/// </para>
/// <para>
/// Delivery may be at most once and may include the publisher: a dropped message costs a live query
/// nothing worse than waiting for its poll, and <see cref="ScryChange.Origin"/> is what a node
/// recognises its own by. A deployment whose database says when it was last written needs none of
/// this — <c>ScryOptions.ChangeProbe</c> reads that instead, and the database is the backplane.
/// </para>
/// </remarks>
public interface IScryChangeBackplane
{
    /// <summary>Tells every other node what this one wrote.</summary>
    ValueTask PublishAsync(ScryChange change, Cancel cancel);

    /// <summary>
    /// Starts handing <paramref name="handler"/> what other nodes publish, until the result is
    /// disposed.
    /// </summary>
    ValueTask<IAsyncDisposable> SubscribeAsync(Func<ScryChange, Cancel, ValueTask> handler, Cancel cancel);
}
