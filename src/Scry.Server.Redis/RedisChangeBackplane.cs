/// <summary>
/// <see cref="IScryChangeBackplane"/> over Redis pub/sub. A change travels as the text
/// <see cref="ScryChange.Serialize"/> writes, on the one channel every node of the deployment shares.
/// </summary>
/// <remarks>
/// Redis pub/sub is at most once: a node that is reconnecting when something is published never hears
/// it. That costs a live query nothing worse than waiting for its poll, which is what the poll is for
/// — and is the reason this needs no delivery guarantee the transport does not have.
/// </remarks>
sealed class RedisChangeBackplane(IRedisPubSub redis, ScryRedisOptions options) :
    IScryChangeBackplane
{
    long dropped;

    /// <summary>How many messages on the channel were not changes, and were ignored.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    public async ValueTask PublishAsync(ScryChange change, Cancel cancel) =>
        await redis.PublishAsync(options.Channel, change.Serialize());

    public async ValueTask<IAsyncDisposable> SubscribeAsync(Func<ScryChange, Cancel, ValueTask> handler, Cancel cancel) =>
        await redis.SubscribeAsync(
            options.Channel,
            async payload =>
            {
                // A channel is shared infrastructure. What is not one of these is somebody else's
                // message, or a newer node's, and is no reason to fault.
                if (!ScryChange.TryParse(payload, out var change))
                {
                    Interlocked.Increment(ref dropped);
                    return;
                }

                await handler(change, Cancel.None);
            });
}
