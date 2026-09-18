using MessagePipe;

/// <summary>
/// The MessagePipe backplane over MessagePipe's own in-memory distributed broker — the reference
/// implementation of the interfaces every real transport of its implements, so what holds here holds
/// over Redis or NATS for as far as this adapter is concerned.
/// </summary>
[TestFixture]
public class MessagePipeBackplaneTests
{
    [Test]
    public async Task AChangePublishedOnOneNodeReachesTheOthers()
    {
        await using var services = Services();
        var here = Backplane(services);
        var there = Backplane(services);
        List<ScryChange> heard = [];
        await using var listening = await there.SubscribeAsync(
            (change, _) =>
            {
                heard.Add(change);
                return ValueTask.CompletedTask;
            },
            default);

        var change = new ScryChange(["Sample.Order", "ArticleLabel (Dictionary<string, object>)"], Guid.NewGuid());
        await here.PublishAsync(change, default);

        Assert.Multiple(() =>
        {
            Assert.That(heard.Single().Entities, Is.EqualTo(change.Entities));
            Assert.That(heard.Single().Origin, Is.EqualTo(change.Origin));
        });
    }

    [Test]
    public async Task WhatIsNotAChangeIsDroppedAndCounted()
    {
        await using var services = Services();
        var node = Backplane(services);
        List<ScryChange> heard = [];
        await using var listening = await node.SubscribeAsync(
            (change, _) =>
            {
                heard.Add(change);
                return ValueTask.CompletedTask;
            },
            default);

        await services
            .GetRequiredService<IDistributedPublisher<string, string>>()
            .PublishAsync("scry:changes", "not a change");

        Assert.Multiple(() =>
        {
            Assert.That(heard, Is.Empty);
            Assert.That(node.Dropped, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TwoDeploymentsOnOneTransportKeepToTheirOwnTopics()
    {
        await using var services = Services();
        var ours = Backplane(services, "ours");
        var theirs = Backplane(services, "theirs");
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
        await using var services = Services();
        var node = Backplane(services);
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

    // Registration resolves MessagePipe's own services from the host, whichever transport backs them.
    [Test]
    public async Task TheRegistrationBuildsABackplaneFromTheHostsMessagePipe()
    {
        var collection = new ServiceCollection();
        collection.AddMessagePipe(_ => _.EnableAutoRegistration = false).AddInMemoryDistributedMessageBroker();
        collection.AddScryChanges();
        collection.AddScryMessagePipeBackplane(_ => _.Topic = "custom");
        await using var services = collection.BuildServiceProvider();

        Assert.That(services.GetRequiredService<IScryChangeBackplane>(), Is.InstanceOf<MessagePipeChangeBackplane>());
    }

    static ServiceProvider Services()
    {
        var collection = new ServiceCollection();
        collection.AddMessagePipe(_ => _.EnableAutoRegistration = false).AddInMemoryDistributedMessageBroker();
        return collection.BuildServiceProvider();
    }

    static MessagePipeChangeBackplane Backplane(IServiceProvider services, string? topic = null)
    {
        var options = new ScryMessagePipeOptions();
        if (topic is not null)
        {
            options.Topic = topic;
        }

        return new(
            services.GetRequiredService<IDistributedPublisher<string, string>>(),
            services.GetRequiredService<IDistributedSubscriber<string, string>>(),
            options);
    }
}
