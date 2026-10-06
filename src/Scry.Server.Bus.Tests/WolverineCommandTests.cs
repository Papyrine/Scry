using Wolverine;
using Wolverine.ErrorHandling;

/// <summary>
/// Commands over Wolverine's local queues, the server and its handlers in one application: the command
/// sent with its headers, handled by an ordinary handler, and finished on the server by the response
/// the middleware sends — or, where the error policy sends the message to the error queue, by the
/// failure it adds.
/// </summary>
// A group of its own, so nothing else in the assembly runs beside it: Wolverine compiles its handler
// chains with Roslyn on a host's first message, and on a starved runner that compile took the pool the
// other buses' commands were waiting on.
[ParallelGroup("Wolverine")]
public class WolverineCommandTests
{
    // One host for each window rather than one a test: Wolverine compiles a host's handler chains on its
    // first message, one host at a time, so five hosts sending at once each waited out all five compiles.
    static IHost host = null!;
    static IHost pendingHost = null!;

    [Before(Class)]
    public static async Task StartHosts()
    {
        host = await Start();
        pendingHost = await Start(window: TimeSpan.Zero);
    }

    [After(Class)]
    public static async Task StopHosts()
    {
        await pendingHost.StopAsync();
        await host.StopAsync();
        pendingHost.Dispose();
        host.Dispose();
    }

    [Test]
    public async Task SendsThroughTheBusAndCompletesWithinTheWindow()
    {
        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "wolverine-within"});

        await Assert.That(receipts.Select(_ => _.Status)).IsEquivalentTo([CommandStatus.Completed], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARemoteWorkerCompletesTheCommand()
    {
        var receipts = await ScryServer.Send(pendingHost.Services, "ShipParcel", new {label = "wolverine-pending"});

        await Assert.That(receipts.Select(_ => _.Status)).IsEquivalentTo([CommandStatus.Pending, CommandStatus.Completed], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CarriesTheHeaders()
    {
        var id = Guid.NewGuid();

        await ScryServer.Send(host.Services, "ShipParcel", new {label = "wolverine-headers"}, id, caller: "alice");

        await Assert.That(Seen.For("wolverine-headers")).IsEqualTo((id.ToString("D"), "alice"));
    }

    [Test]
    public async Task ATypedResultTravelsBack()
    {
        var receipts = await ScryServer.Send(host.Services, "WeighParcel", new {grams = 21});

        await Assert.That(receipts.Last().Result!.Value.GetProperty("grams").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task AHandlerThatExhaustsRetriesIsReportedAsFailed()
    {
        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "wolverine-failing", fail = true});

        using (Assert.Multiple())
        {
            await Assert.That(receipts.Last().Status).IsEqualTo(CommandStatus.Failed);
            await Assert.That(receipts.Last().Error).IsEqualTo("Command execution failed.");
        }
    }

    [Test]
    public async Task TheAdapterClaimsOnlyWhatItWasTold()
    {
        var dispatcher = host.Services.GetRequiredService<WolverineDispatcher>();

        using (Assert.Multiple())
        {
            await Assert.That(dispatcher.CanDispatch(typeof(ShipParcel))).IsTrue();
            await Assert.That(dispatcher.CanDispatch(typeof(LocalChore))).IsFalse();
        }
    }

    [Test]
    public async Task TwoAdaptersClaimingOneCommandRefuseStartup()
    {
        using var second = await Start(second: true);

        var exception = Assert.ThrowsExactly<Exception>(() => ScryServer.EnsureDispatchable(second.Services));

        await Assert.That(exception.Message).Contains("claimed by");
    }

    static async Task<IHost> Start(TimeSpan? window = null, bool second = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        ScryServer.Add(builder.Services, window, _ => _.UseWolverineCommands(_ => _.For<ShipParcel>().For<WeighParcel>()), second);
        builder.UseWolverine(
            _ =>
            {
                // Only these: the test assembly also holds the other buses' handlers and consumers.
                _.Discovery.DisableConventionalDiscovery();
                _.Discovery.IncludeType(typeof(ShipParcelWolverineHandler));
                _.Discovery.IncludeType(typeof(WeighParcelWolverineHandler));
                _.AddScryCommandCompletions();
                _.UseScryCommands();
                _.Policies.OnAnyException().MoveToErrorQueue().AndScryFailure();
            });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }
}

public static class ShipParcelWolverineHandler
{
    public static void Handle(ShipParcel message, Envelope envelope)
    {
        Seen.Add(message.Label, envelope.Headers.GetValueOrDefault(ScryCommandHeaders.CommandId), envelope.Headers.GetValueOrDefault(ScryCommandHeaders.Caller));
        if (message.Fail)
        {
            throw new InvalidOperationException("The parcel could not be shipped.");
        }
    }
}

public static class WeighParcelWolverineHandler
{
    public static void Handle(WeighParcel message, IMessageContext context) =>
        context.SetScryResult(
            new Weighed
            {
                Grams = message.Grams * 2
            });
}
