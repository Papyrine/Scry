/// <summary>
/// As much of Redis as the backplane uses: say something on a channel, and hear what is said on one.
/// A seam rather than the client's own interface because that one has dozens of members, and a fake
/// of it would say more about the fake than about what is under test.
/// </summary>
interface IRedisPubSub
{
    Task PublishAsync(string channel, string payload);

    Task<IAsyncDisposable> SubscribeAsync(string channel, Func<string, Task> handler);
}
