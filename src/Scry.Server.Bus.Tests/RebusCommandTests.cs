using Rebus.Config;
using Rebus.Handlers;
using Rebus.Pipeline;
using Rebus.Retry.Simple;
using Rebus.Routing.TypeBased;
using Rebus.Transport.InMem;

/// <summary>
/// Commands over a Rebus in-memory network between two hosts, as two processes would be: the server,
/// which sends and hears replies on an input queue of its own, and a worker, whose handlers are ordinary
/// Rebus handlers and whose replies leave inside each message's transaction.
/// </summary>
[TestFixture]
public class RebusCommandTests
{
    [Test]
    public async Task SendsThroughTheBusAndCompletesWithinTheWindow()
    {
        await using var pair = await Pair.Start();

        var receipts = await ScryServer.Send(pair.Server.Services, "ShipParcel", new {label = "rebus-within"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Completed]));
    }

    [Test]
    public async Task ARemoteWorkerCompletesTheCommand()
    {
        await using var pair = await Pair.Start(window: TimeSpan.Zero);

        var receipts = await ScryServer.Send(pair.Server.Services, "ShipParcel", new {label = "rebus-pending"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Pending, CommandStatus.Completed]));
    }

    [Test]
    public async Task CarriesTheHeaders()
    {
        await using var pair = await Pair.Start();
        var id = Guid.NewGuid();

        await ScryServer.Send(pair.Server.Services, "ShipParcel", new {label = "rebus-headers"}, id, caller: "alice");

        Assert.That(Seen.For("rebus-headers"), Is.EqualTo((id.ToString("D"), "alice")));
    }

    [Test]
    public async Task ATypedResultTravelsBack()
    {
        await using var pair = await Pair.Start();

        var receipts = await ScryServer.Send(pair.Server.Services, "WeighParcel", new {grams = 21});

        Assert.That(receipts.Last().Result!.Value.GetProperty("grams").GetInt32(), Is.EqualTo(42));
    }

    [Test]
    public async Task AHandlerThatExhaustsRetriesIsReportedAsFailed()
    {
        await using var pair = await Pair.Start();

        var receipts = await ScryServer.Send(pair.Server.Services, "ShipParcel", new {label = "rebus-failing", fail = true});

        Assert.Multiple(() =>
        {
            Assert.That(receipts.Last().Status, Is.EqualTo(CommandStatus.Failed));
            Assert.That(receipts.Last().Error, Is.EqualTo("Command execution failed."));
        });
    }

    [Test]
    public async Task TheAdapterClaimsOnlyWhatItWasTold()
    {
        await using var pair = await Pair.Start();
        var dispatcher = pair.Server.Services.GetRequiredService<RebusDispatcher>();

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch(typeof(ShipParcel)), Is.True);
            Assert.That(dispatcher.CanDispatch(typeof(LocalChore)), Is.False);
        });
    }

    [Test]
    public async Task TwoAdaptersClaimingOneCommandRefuseStartup()
    {
        await using var pair = await Pair.Start(second: true);

        var exception = Assert.Throws<Exception>(() => ScryServer.EnsureDispatchable(pair.Server.Services))!;

        Assert.That(exception.Message, Does.Contain("claimed by"));
    }

    sealed class Pair(IHost server, IHost worker) :
        IAsyncDisposable
    {
        public IHost Server => server;

        public static async Task<Pair> Start(TimeSpan? window = null, bool second = false)
        {
            var network = new InMemNetwork();
            var queue = $"scry-server-{Guid.NewGuid():N}";
            var workerQueue = $"scry-worker-{Guid.NewGuid():N}";

            var serverBuilder = Host.CreateApplicationBuilder();
            serverBuilder.Logging.ClearProviders();
            ScryServer.Add(serverBuilder.Services, window, _ => _.UseRebusCommands(_ => _.For<ShipParcel>().For<WeighParcel>()), second);
            serverBuilder.Services.AddRebusHandler<RebusCommandCompletedHandler>();
            serverBuilder.Services.AddRebus(
                configure => configure
                    .Logging(_ => _.None())
                    .Transport(_ => _.UseInMemoryTransport(network, queue))
                    .Routing(
                        _ => _.TypeBased()
                            .Map<ShipParcel>(workerQueue)
                            .Map<WeighParcel>(workerQueue)));

            var workerBuilder = Host.CreateApplicationBuilder();
            workerBuilder.Logging.ClearProviders();
            workerBuilder.Services.AddRebusHandler<ShipParcelHandler>();
            workerBuilder.Services.AddRebusHandler<WeighParcelHandler>();
            workerBuilder.Services.AddRebus(
                configure => configure
                    .Logging(_ => _.None())
                    .Transport(_ => _.UseInMemoryTransport(network, workerQueue))
                    .Options(
                        _ =>
                        {
                            _.EnableScryCompletion();

                            // One delivery: a test about a failure is over when the handler has thrown once.
                            _.RetryStrategy(maxDeliveryAttempts: 1);
                        }));

            var serverHost = serverBuilder.Build();
            var workerHost = workerBuilder.Build();
            await serverHost.StartAsync();
            await workerHost.StartAsync();
            return new(serverHost, workerHost);
        }

        public async ValueTask DisposeAsync()
        {
            await worker.StopAsync();
            await server.StopAsync();
            worker.Dispose();
            server.Dispose();
        }
    }

    public sealed class ShipParcelHandler :
        IHandleMessages<ShipParcel>
    {
        public Task Handle(ShipParcel message)
        {
            var headers = MessageContext.Current.Headers;
            Seen.Add(message.Label, headers.GetValueOrDefault(ScryCommandHeaders.CommandId), headers.GetValueOrDefault(ScryCommandHeaders.Caller));
            if (message.Fail)
            {
                throw new InvalidOperationException("The parcel could not be shipped.");
            }

            return Task.CompletedTask;
        }
    }

    public sealed class WeighParcelHandler :
        IHandleMessages<WeighParcel>
    {
        public Task Handle(WeighParcel message)
        {
            MessageContext.Current.SetScryResult(
                new Weighed
                {
                    Grams = message.Grams * 2
                });
            return Task.CompletedTask;
        }
    }
}
