using Wolverine;
using Wolverine.ErrorHandling;

/// <summary>
/// Commands over Wolverine's local queues, the server and its handlers in one application: the command
/// sent with its headers, handled by an ordinary handler, and finished on the server by the response
/// the middleware sends — or, where the error policy sends the message to the error queue, by the
/// failure it adds.
/// </summary>
[TestFixture]
public class WolverineCommandTests
{
    [Test]
    public async Task SendsThroughTheBusAndCompletesWithinTheWindow()
    {
        using var host = await Start();

        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "wolverine-within"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Completed]));
    }

    [Test]
    public async Task ARemoteWorkerCompletesTheCommand()
    {
        using var host = await Start(window: TimeSpan.Zero);

        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "wolverine-pending"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Pending, CommandStatus.Completed]));
    }

    [Test]
    public async Task CarriesTheHeaders()
    {
        using var host = await Start();
        var id = Guid.NewGuid();

        await ScryServer.Send(host.Services, "ShipParcel", new {label = "wolverine-headers"}, id, caller: "alice");

        Assert.That(Seen.For("wolverine-headers"), Is.EqualTo((id.ToString("D"), "alice")));
    }

    [Test]
    public async Task ATypedResultTravelsBack()
    {
        using var host = await Start();

        var receipts = await ScryServer.Send(host.Services, "WeighParcel", new {grams = 21});

        Assert.That(receipts.Last().Result!.Value.GetProperty("grams").GetInt32(), Is.EqualTo(42));
    }

    [Test]
    public async Task AHandlerThatExhaustsRetriesIsReportedAsFailed()
    {
        using var host = await Start();

        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "wolverine-failing", fail = true});

        Assert.Multiple(() =>
        {
            Assert.That(receipts.Last().Status, Is.EqualTo(CommandStatus.Failed));
            Assert.That(receipts.Last().Error, Is.EqualTo("Command execution failed."));
        });
    }

    [Test]
    public async Task TheAdapterClaimsOnlyWhatItWasTold()
    {
        using var host = await Start();
        var dispatcher = host.Services.GetRequiredService<WolverineDispatcher>();

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch(typeof(ShipParcel)), Is.True);
            Assert.That(dispatcher.CanDispatch(typeof(LocalChore)), Is.False);
        });
    }

    [Test]
    public async Task TwoAdaptersClaimingOneCommandRefuseStartup()
    {
        using var host = await Start(second: true);

        var exception = Assert.Throws<Exception>(() => ScryServer.EnsureDispatchable(host.Services))!;

        Assert.That(exception.Message, Does.Contain("claimed by"));
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
