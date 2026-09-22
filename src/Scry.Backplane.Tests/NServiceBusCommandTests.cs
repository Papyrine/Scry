using System.Collections.Concurrent;
using System.Text.Json;
using NServiceBus;
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// Commands over NServiceBus between real endpoints in this process, over the learning transport: a
/// Scry server that dispatches, and a worker that handles and replies. What is pinned is the round
/// trip — sent with its headers, handled by an ordinary handler, finished on the server by the reply —
/// and when the reply leaves: after the handlers, with what they published, and never for an attempt
/// that failed.
/// </summary>
/// <remarks>
/// The server's commands are untargeted, so its context is never opened: its model is all it is for.
/// </remarks>
[TestFixture]
public class NServiceBusCommandTests
{
    const string workerName = "ScryTests.CommandWorker";

    string storage = null!;
    Worker worker = null!;

    [OneTimeSetUp]
    public async Task StartWorker()
    {
        storage = Path.Combine(Path.GetTempPath(), $"scry-commands-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);
        worker = await Worker.Start(storage);
    }

    [OneTimeTearDown]
    public async Task StopWorker()
    {
        await worker.DisposeAsync();
        try
        {
            Directory.Delete(storage, recursive: true);
        }
        catch (IOException)
        {
            // The transport may still hold a file for a moment; the folder is a temporary one.
        }
    }

    [Test]
    public async Task SendsThroughTheBusAndCompletesWithinTheWindow()
    {
        await using var server = await Server.Start("ScryTests.CommandsWithinWindow", storage, window: TimeSpan.FromSeconds(20));

        var receipts = await server.Send("ShipParcel", new {label = "Within"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Completed]));
    }

    // Past the window: pending at once, and finished when the reply comes in.
    [Test]
    public async Task ARemoteWorkerCompletesTheCommand()
    {
        await using var server = await Server.Start("ScryTests.CommandsPending", storage, window: TimeSpan.Zero);

        var receipts = await server.Send("ShipParcel", new {label = "Pending"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Pending, CommandStatus.Completed]));
    }

    [Test]
    public async Task CarriesTheHeaders()
    {
        await using var server = await Server.Start("ScryTests.CommandsHeaders", storage);
        var id = Guid.NewGuid();

        await server.Send("ShipParcel", new {label = "Headers"}, id, caller: "alice");

        var seen = ShipParcelHandler.Seen.Single(_ => _.Label == "Headers");
        Assert.Multiple(() =>
        {
            Assert.That(seen.CommandId, Is.EqualTo(id.ToString("D")));
            Assert.That(seen.Caller, Is.EqualTo("alice"));
        });
    }

    [Test]
    public async Task ATypedResultTravelsBack()
    {
        await using var server = await Server.Start("ScryTests.CommandsResult", storage);

        var receipts = await server.Send("WeighParcel", new {grams = 21});

        var final = receipts.Last();
        Assert.Multiple(() =>
        {
            Assert.That(final.Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(final.Result!.Value.GetProperty("grams").GetInt32(), Is.EqualTo(42));
        });
    }

    // Every attempt spent, the message goes to the error queue — and the server is told it failed,
    // in the fixed words, rather than left waiting until it gives up.
    [Test]
    public async Task AHandlerThatExhaustsRetriesIsReportedAsFailed()
    {
        await using var server = await Server.Start("ScryTests.CommandsFailing", storage);

        var receipts = await server.Send("ShipParcel", new {label = "Failing", fail = true});

        var final = receipts.Last();
        Assert.Multiple(() =>
        {
            Assert.That(final.Status, Is.EqualTo(CommandStatus.Failed));
            Assert.That(final.Error, Is.EqualTo("Command execution failed."));
        });
    }

    // The reply and the change leave through the same context, after the same handlers.
    [Test]
    public async Task TheReplyAndTheChangeLeaveTogether()
    {
        await using var server = await Server.Start("ScryTests.CommandsChanges", storage);

        var receipts = await server.Send("ShipParcel", new {label = "Together"});
        var change = await server.Heard.Next();

        Assert.Multiple(() =>
        {
            Assert.That(receipts.Last().Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(change.Entities, Is.EqualTo(["Parcel"]));
        });
    }

    // By default, what the endpoint calls a command by marker; beside that, only what it was told.
    [Test]
    public async Task TheAdapterClaimsOnlyWhatItWasTold()
    {
        await using var bare = await Server.Start("ScryTests.CommandsClaimsBare", storage);
        await using var told = await Server.Start("ScryTests.CommandsClaimsTold", storage, claims: _ => _.For<NamedChore>());

        Assert.Multiple(() =>
        {
            Assert.That(bare.Claims(typeof(ShipParcel)), Is.True);
            Assert.That(bare.Claims(typeof(NamedChore)), Is.False);
            Assert.That(bare.Claims(typeof(LocalChore)), Is.False);
            Assert.That(told.Claims(typeof(NamedChore)), Is.True);
            Assert.That(told.Claims(typeof(LocalChore)), Is.False);
        });
    }

    // What no dispatcher claims stays with its in-process handler.
    [Test]
    public async Task AnUnclaimedCommandIsHandledInProcess()
    {
        await using var server = await Server.Start("ScryTests.CommandsLocal", storage);

        var receipts = await server.Send("LocalChore", new { });

        Assert.That(receipts.Last().Status, Is.EqualTo(CommandStatus.Completed));
    }

    [Test]
    public async Task TwoAdaptersClaimingOneCommandRefuseStartup()
    {
        await using var server = await Server.Start("ScryTests.CommandsTwoClaims", storage, second: true);

        var exception = Assert.Throws<Exception>(server.EnsureDispatchable)!;

        Assert.That(exception.Message, Does.Contain("claimed by"));
    }

    /// <summary>The worker: handles the commands, replies, and publishes what it saved.</summary>
    sealed class Worker(IHost host) :
        IAsyncDisposable
    {
        public static async Task<Worker> Start(string storage)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddScryNServiceBusBackplane();
            var configuration = Endpoint(workerName, storage);
            configuration.UseScryChanges();
            configuration.UseScryCommands();
            builder.Services.AddNServiceBusEndpoint(configuration);
            var host = builder.Build();
            await host.StartAsync();
            return new(host);
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    /// <summary>A Scry server that dispatches over its own endpoint and hears the replies on it.</summary>
    sealed class Server(IHost host) :
        IAsyncDisposable
    {
        public Heard Heard { get; } = new();

        ScryProcessor Processor => host.Services.GetRequiredService<ScryProcessor>();

        public static async Task<Server> Start(
            string name,
            string storage,
            TimeSpan? window = null,
            Action<BusCommands>? claims = null,
            bool second = false)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();

            // Only ever asked for its model: every command here is untargeted.
            builder.Services.AddDbContext<BusContext>(_ => _.UseSqlServer("Server=.;Database=NeverOpened"));
            builder.Services.AddScry<BusContext>(options =>
            {
                options.AllowUnmappedSources = true;
                options.MaxPendingCommands = 10;
                options.CommandSyncWindow = window ?? TimeSpan.FromSeconds(20);
                options.UseNServiceBusCommands(claims);
                options.UseNServiceBusBackplane();
                if (second)
                {
                    options.AddDispatcher<ClaimsEverything>();
                }
            });
            builder.Services.AddSingleton<ClaimsEverything>();
            builder.Services.AddScoped<ICommandHandler<LocalChore>, LocalChoreHandler>();

            var configuration = Endpoint(name, storage);
            builder.Services.AddNServiceBusEndpoint(configuration);
            var host = builder.Build();
            await host.StartAsync();

            var server = new Server(host);
            var changes = host.Services.GetRequiredService<ScryChanges>();
            changes.Listen(server.Heard.Add);
            await changes.Reconciled;
            return server;
        }

        public async Task<List<CommandReceipt>> Send(string command, object payload, Guid? id = null, string? caller = null)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var request = CommandRequest.Create(command, id ?? Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, ScryJson.Options));
            List<CommandReceipt> receipts = [];
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await foreach (var receipt in Processor.SendCommand(request, services.GetRequiredService<BusContext>(), services, new Microsoft.AspNetCore.Http.HeaderDictionary(), caller, patience.Token))
            {
                receipts.Add(receipt);
            }

            return receipts;
        }

        // What the adapter's own dispatcher, as UseNServiceBusCommands registered it, says it carries.
        public bool Claims(Type command) =>
            host.Services.GetRequiredService<NServiceBusDispatcher>().CanDispatch(command);

        public void EnsureDispatchable() =>
            Processor.EnsureCommandsDispatchable(host.Services);

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    static EndpointConfiguration Endpoint(string name, string storage)
    {
        var configuration = new EndpointConfiguration(name);
        configuration.UseSerialization<SystemJsonSerializer>();
        var routing = configuration.UseTransport(
            new LearningTransport
            {
                StorageDirectory = storage
            });
        routing.RouteToEndpoint(typeof(ShipParcel), workerName);
        routing.RouteToEndpoint(typeof(WeighParcel), workerName);
        routing.RouteToEndpoint(typeof(NamedChore), workerName);
        configuration.Conventions().DefiningCommandsAs(_ => _ == typeof(NamedChore));
        configuration.SendFailedMessagesTo("ScryTests.CommandErrors");
        configuration.EnableInstallers();

        // A failure is left failed, so a test about one is over when the handler has thrown once.
        configuration.Recoverability().Immediate(_ => _.NumberOfRetries(0));
        configuration.Recoverability().Delayed(_ => _.NumberOfRetries(0));
        return configuration;
    }

    sealed class ClaimsEverything :
        ICommandDispatcher
    {
        public bool CanDispatch(Type command) => true;

        public Task Dispatch(CommandEnvelope envelope, CancellationToken cancel) =>
            Task.CompletedTask;
    }

    sealed class Heard
    {
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

        public async Task<ScryChange> Next()
        {
            Assert.That(await arrived.WaitAsync(TimeSpan.FromSeconds(30)), Is.True, "No change arrived.");
            lock (changes)
            {
                return changes.Dequeue();
            }
        }
    }
}

public sealed class BusContext(Microsoft.EntityFrameworkCore.DbContextOptions<BusContext> options) :
    Microsoft.EntityFrameworkCore.DbContext(options);

/// <summary>A command the endpoint knows as one by marker, so claimed by default.</summary>
[Command]
public sealed class ShipParcel :
    ICommand
{
    public string Label { get; set; } = "";
    public bool Fail { get; set; }
}

[Command(Result = typeof(Weighed))]
public sealed class WeighParcel :
    ICommand
{
    public int Grams { get; set; }
}

public sealed class Weighed
{
    public int Grams { get; set; }
}

/// <summary>A command by the endpoint's convention alone, claimed only when named.</summary>
[Command]
public sealed class NamedChore;

/// <summary>Neither: handled where the server is.</summary>
[Command]
public sealed class LocalChore;

public sealed class LocalChoreHandler :
    ICommandHandler<LocalChore>
{
    public Task Handle(LocalChore command, ScryCommandContext context, CancellationToken cancel) =>
        Task.CompletedTask;
}

/// <summary>An ordinary handler: it saves, which is reported, and throws when told to.</summary>
public sealed class ShipParcelHandler(ScryChanges changes) :
    IHandleMessages<ShipParcel>
{
    public static ConcurrentQueue<(string Label, string? CommandId, string? Caller)> Seen { get; } = new();

    public Task Handle(ShipParcel message, IMessageHandlerContext context)
    {
        Seen.Enqueue((
            message.Label,
            context.MessageHeaders.GetValueOrDefault(ScryCommandHeaders.CommandId),
            context.MessageHeaders.GetValueOrDefault(ScryCommandHeaders.Caller)));
        changes.Raise(["Parcel"]);
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
    public Task Handle(WeighParcel message, IMessageHandlerContext context)
    {
        context.SetScryResult(
            new Weighed
            {
                Grams = message.Grams * 2
            });
        return Task.CompletedTask;
    }
}
