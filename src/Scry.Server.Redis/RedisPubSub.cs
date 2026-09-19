/// <summary>The seam over a real connection. Everything that is Redis, and nothing that is not.</summary>
sealed class RedisPubSub(IConnectionMultiplexer connection) :
    IRedisPubSub
{
    public Task PublishAsync(string channel, string payload) =>
        connection
            .GetSubscriber()
            .PublishAsync(RedisChannel.Literal(channel), payload);

    public async Task<IAsyncDisposable> SubscribeAsync(string channel, Func<string, Task> handler)
    {
        var queue = await connection
            .GetSubscriber()
            .SubscribeAsync(RedisChannel.Literal(channel));

        // Handled one at a time and in order, which is what a queue's asynchronous handler gives: a
        // change is a flag being set, so there is nothing to gain from handling two at once.
        queue.OnMessage(message => handler(message.Message.ToString()));
        return new Subscription(queue);
    }

    sealed class Subscription(ChannelMessageQueue queue) :
        IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await queue.UnsubscribeAsync();
    }
}
