using StackExchange.Redis;

/// <summary>
/// The Redis backplane against a stand-in for Redis: what it publishes, what it hands a handler, and
/// what it drops. The dozen lines that talk to a real server are behind the seam this fakes, and are
/// exercised by the one test here that wants a server and is skipped without one.
/// </summary>
[TestFixture]
public class RedisBackplaneTests
{
    [Test]
    public async Task AChangePublishedOnOneNodeReachesTheOthers()
    {
        var redis = new FakeRedis();
        var here = new RedisChangeBackplane(redis, new());
        var there = new RedisChangeBackplane(redis, new());
        List<ScryChange> heard = [];
        await using var listening = await there.SubscribeAsync(
            (change, _) =>
            {
                heard.Add(change);
                return ValueTask.CompletedTask;
            },
            default);

        var change = new ScryChange(["Sample.Order", "Sample.OrderLine"], Guid.NewGuid());
        await here.PublishAsync(change, default);

        Assert.Multiple(() =>
        {
            Assert.That(heard.Single().Entities, Is.EqualTo(change.Entities));
            Assert.That(heard.Single().Origin, Is.EqualTo(change.Origin));
        });
    }

    // Delivered to the publisher too, as Redis does. Recognising its own is the listener's job, by
    // the origin the change carries — which is why the change carries one.
    [Test]
    public async Task APublisherHearsItsOwnChangeWithItsOriginIntact()
    {
        var redis = new FakeRedis();
        var node = new RedisChangeBackplane(redis, new());
        List<ScryChange> heard = [];
        await using var listening = await node.SubscribeAsync(
            (change, _) =>
            {
                heard.Add(change);
                return ValueTask.CompletedTask;
            },
            default);
        var origin = Guid.NewGuid();

        await node.PublishAsync(new([], origin), default);

        Assert.Multiple(() =>
        {
            Assert.That(heard.Single().Origin, Is.EqualTo(origin));
            Assert.That(heard.Single().Everything, Is.True);
        });
    }

    // A channel is shared infrastructure: what is not a change is somebody else's message.
    [Test]
    public async Task WhatIsNotAChangeIsDroppedAndCounted()
    {
        var redis = new FakeRedis();
        var node = new RedisChangeBackplane(redis, new());
        List<ScryChange> heard = [];
        await using var listening = await node.SubscribeAsync(
            (change, _) =>
            {
                heard.Add(change);
                return ValueTask.CompletedTask;
            },
            default);

        await redis.PublishAsync("scry:changes", "not a change");

        Assert.Multiple(() =>
        {
            Assert.That(heard, Is.Empty);
            Assert.That(node.Dropped, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TwoDeploymentsOnOneRedisKeepToTheirOwnChannels()
    {
        var redis = new FakeRedis();
        var ours = new RedisChangeBackplane(
            redis,
            new()
            {
                Channel = "ours"
            });
        var theirs = new RedisChangeBackplane(
            redis,
            new()
            {
                Channel = "theirs"
            });
        List<ScryChange> heard = [];
        await using var listening = await theirs.SubscribeAsync(
            (change, _) =>
            {
                heard.Add(change);
                return ValueTask.CompletedTask;
            },
            default);

        await ours.PublishAsync(new(["Sample.Order"], Guid.NewGuid()), default);

        Assert.That(heard, Is.Empty);
    }

    [Test]
    public async Task ASubscriptionThatWasDisposedHearsNothingMore()
    {
        var redis = new FakeRedis();
        var node = new RedisChangeBackplane(redis, new());
        List<ScryChange> heard = [];
        var listening = await node.SubscribeAsync(
            (change, _) =>
            {
                heard.Add(change);
                return ValueTask.CompletedTask;
            },
            default);

        await listening.DisposeAsync();
        await node.PublishAsync(new([], Guid.NewGuid()), default);

        Assert.That(heard, Is.Empty);
    }

    // Registration: the backplane is built from the host's services, which is where the connection is.
    [Test]
    public void TheOptionRegistersABackplaneBuiltFromTheHostsConnection()
    {
        var services = new ServiceCollection();
        services.AddScryChanges();
        services.AddScryRedisBackplane(_ => _.Channel = "custom");

        Assert.That(
            services.Single(_ => _.ServiceType == typeof(IScryChangeBackplane)).Lifetime,
            Is.EqualTo(ServiceLifetime.Singleton));
    }

    // The dozen lines behind the seam, against a real server. Set ScryRedis to a connection string —
    // "localhost:6379" against `docker run -p 6379:6379 redis` — to run it.
    [Test]
    public async Task ARealServerCarriesAChange()
    {
        if (Environment.GetEnvironmentVariable("ScryRedis") is not {Length: > 0} connectionString)
        {
            Assert.Ignore("Set the ScryRedis environment variable to a Redis connection string to run this.");
            return;
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var options = new ScryRedisOptions
        {
            Channel = $"scry:test:{Guid.NewGuid():N}"
        };
        var here = new RedisChangeBackplane(new RedisPubSub(connection), options);
        var there = new RedisChangeBackplane(new RedisPubSub(connection), options);
        var heard = new TaskCompletionSource<ScryChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listening = await there.SubscribeAsync(
            (change, _) =>
            {
                heard.TrySetResult(change);
                return ValueTask.CompletedTask;
            },
            default);

        var change = new ScryChange(["Sample.Order"], Guid.NewGuid());
        await here.PublishAsync(change, default);

        var received = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(received.Entities, Is.EqualTo(change.Entities));
    }

    /// <summary>Delivers to every subscriber of a channel, the publisher included, as Redis does.</summary>
    sealed class FakeRedis :
        IRedisPubSub
    {
        List<(string Channel, Func<string, Task> Handler)> subscribers = [];

        public async Task PublishAsync(string channel, string payload)
        {
            Func<string, Task>[] handlers;
            lock (subscribers)
            {
                handlers = [.. subscribers.Where(_ => _.Channel == channel).Select(_ => _.Handler)];
            }

            foreach (var handler in handlers)
            {
                await handler(payload);
            }
        }

        public Task<IAsyncDisposable> SubscribeAsync(string channel, Func<string, Task> handler)
        {
            lock (subscribers)
            {
                subscribers.Add((channel, handler));
            }

            return Task.FromResult<IAsyncDisposable>(new Subscription(this, handler));
        }

        sealed class Subscription(FakeRedis owner, Func<string, Task> handler) :
            IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                lock (owner.subscribers)
                {
                    owner.subscribers.RemoveAll(_ => _.Handler == handler);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
