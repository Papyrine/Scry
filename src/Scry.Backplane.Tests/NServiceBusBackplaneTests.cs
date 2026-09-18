using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NServiceBus;

/// <summary>
/// The NServiceBus backplane between real endpoints: two that serve live queries and a worker that
/// only writes, all in this process, over the learning transport — files in a temporary folder, so
/// nothing is needed that the build agent does not have.
/// </summary>
/// <remarks>
/// What matters most here is when a worker's change is published: once per incoming message, after
/// its handlers are done, and not at all if they threw. That is what makes the event mean "this was
/// written" rather than "somebody tried to write this".
/// </remarks>
[TestFixture]
public class NServiceBusBackplaneTests
{
    string storage = null!;
    Node webA = null!;
    Node webB = null!;
    Node worker = null!;

    [OneTimeSetUp]
    public async Task StartEndpoints()
    {
        storage = Path.Combine(Path.GetTempPath(), $"scry-backplane-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);

        // Each server is an endpoint of its own. NServiceBus hands an event to one instance of each
        // endpoint, so two servers sharing a name would share the events between them.
        webA = await Node.Start("ScryTests.WebA", storage, worker: false);
        webB = await Node.Start("ScryTests.WebB", storage, worker: false);
        worker = await Node.Start("ScryTests.Worker", storage, worker: true);
    }

    [OneTimeTearDown]
    public async Task StopEndpoints()
    {
        await worker.DisposeAsync();
        await webB.DisposeAsync();
        await webA.DisposeAsync();
        try
        {
            Directory.Delete(storage, recursive: true);
        }
        catch (IOException)
        {
            // The transport may still hold a file for a moment; the folder is a temporary one.
        }
    }

    [SetUp]
    public void Forget()
    {
        webA.Heard.Clear();
        webB.Heard.Clear();
    }

    // The reason to want this adapter: a write in another process, which no interceptor on the
    // server could ever see, reaching it with the names of what was written.
    [Test]
    public async Task WhatAWorkersHandlerSavedReachesEveryServer()
    {
        await webA.Session.Send(
            "ScryTests.Worker",
            new DoWork
            {
                Entities = ["Sample.Order"]
            });

        var atA = await webA.Heard.Next();
        var atB = await webB.Heard.Next();

        Assert.Multiple(() =>
        {
            Assert.That(atA.Entities, Is.EqualTo(["Sample.Order"]));
            Assert.That(atB.Entities, Is.EqualTo(["Sample.Order"]));
            Assert.That(atA.Origin, Is.EqualTo(worker.Changes.Origin));
        });
    }

    // Two saves while handling one message are one thing that happened, published once.
    [Test]
    public async Task OneMessageIsOneEventHoweverManyTimesItsHandlerSaved()
    {
        await webA.Session.Send(
            "ScryTests.Worker",
            new DoWork
            {
                Entities = ["Sample.Order", "Sample.OrderLine"],
                SaveEachSeparately = true
            });

        var heard = await webA.Heard.Next();
        await webA.Heard.NothingMore();

        Assert.That(heard.Entities, Is.EquivalentTo(["Sample.Order", "Sample.OrderLine"]));
    }

    // Published through the message's own context, so it leaves only if the handler's work is kept.
    [Test]
    public async Task AHandlerThatThrewPublishesNothing()
    {
        await webA.Session.Send(
            "ScryTests.Worker",
            new DoWork
            {
                Entities = ["Sample.Order"],
                ThenThrow = true
            });

        await webA.Heard.NothingMore();
    }

    [Test]
    public async Task AServersOwnChangeReachesTheOtherServerAndIsNotHeardTwice()
    {
        webA.Changes.Raise(["Sample.Department"]);

        var atB = await webB.Heard.Next();
        var atA = await webA.Heard.Next();
        await webA.Heard.NothingMore();

        Assert.Multiple(() =>
        {
            Assert.That(atB.Origin, Is.EqualTo(webA.Changes.Origin));

            // Heard once at home: from the raise itself. The event coming back is recognised.
            Assert.That(atA.Entities, Is.EqualTo(["Sample.Department"]));
        });
    }

    // The option a Scry server sets, which is the same registration reached through AddScry.
    [Test]
    public void TheServerOptionRegistersTheBackplaneAndWhatItShares()
    {
        var options = new ScryOptions(typeof(object));

        options.UseNServiceBusBackplane();

        Assert.Multiple(() =>
        {
            Assert.That(options.Backplane, Is.Not.Null);
            Assert.That(options.BackplaneServices, Is.Not.Null);
        });
    }

    /// <summary>One endpoint in a host of its own, as one process would be.</summary>
    sealed class Node(IHost host) :
        IAsyncDisposable
    {
        public IMessageSession Session => host.Services.GetRequiredService<IMessageSession>();

        public ScryChanges Changes => host.Services.GetRequiredService<ScryChanges>();

        public Heard Heard { get; } = new();

        public static async Task<Node> Start(string name, string storage, bool worker)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddScryNServiceBusBackplane();

            var configuration = new EndpointConfiguration(name);
            configuration.UseSerialization<SystemJsonSerializer>();
            configuration.UseTransport(
                new LearningTransport
                {
                    StorageDirectory = storage
                });
            configuration.SendFailedMessagesTo("ScryTests.Error");
            configuration.EnableInstallers();

            // A failure is left failed, so that a test about one is over when the handler has thrown
            // once rather than after a schedule of retries.
            configuration.Recoverability().Immediate(_ => _.NumberOfRetries(0));
            configuration.Recoverability().Delayed(_ => _.NumberOfRetries(0));
            if (worker)
            {
                configuration.UseScryChanges();
            }

            builder.Services.AddNServiceBusEndpoint(configuration);
            var host = builder.Build();
            await host.StartAsync();

            var node = new Node(host);
            if (!worker)
            {
                // Something has to be listening for a server to hold its place on the backplane, as a
                // live query would be.
                node.Changes.Listen(node.Heard.Add);
                await node.Changes.Reconciled;
            }

            return node;
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    sealed class Heard
    {
        static TimeSpan patience = TimeSpan.FromSeconds(30);
        static TimeSpan quiet = TimeSpan.FromSeconds(2);

        Queue<ScryChange> changes = new();
        SemaphoreSlim arrived = new(0);

        public void Add(ScryChange change)
        {
            lock (changes)
            {
                changes.Enqueue(change);
            }

            arrived.Release();
        }

        public void Clear()
        {
            lock (changes)
            {
                changes.Clear();
            }

            while (arrived.Wait(0))
            {
            }
        }

        public async Task<ScryChange> Next()
        {
            Assert.That(await arrived.WaitAsync(patience), Is.True, "No change arrived.");
            lock (changes)
            {
                return changes.Dequeue();
            }
        }

        public async Task NothingMore() =>
            Assert.That(await arrived.WaitAsync(quiet), Is.False, "A change arrived that should not have.");
    }
}

public sealed class DoWork :
    ICommand
{
    public string[] Entities { get; set; } = [];

    public bool SaveEachSeparately { get; set; }

    public bool ThenThrow { get; set; }
}

/// <summary>
/// Stands in for a handler that saves through a context carrying the change interceptor: what reaches
/// the backplane is the same call, made from the same place — inside the handler.
/// </summary>
public sealed class DoWorkHandler(ScryChanges changes) :
    IHandleMessages<DoWork>
{
    public Task Handle(DoWork message, IMessageHandlerContext context)
    {
        if (message.SaveEachSeparately)
        {
            foreach (var entity in message.Entities)
            {
                changes.Raise([entity]);
            }
        }
        else
        {
            changes.Raise(message.Entities);
        }

        if (message.ThenThrow)
        {
            throw new InvalidOperationException("The handler failed after saving.");
        }

        return Task.CompletedTask;
    }
}
