using MassTransit;

/// <summary>
/// Commands over MassTransit's in-memory transport, the server and its consumers on one bus: the
/// command published with its headers, consumed by an ordinary consumer, and finished on the server by
/// the completion the filter publishes.
/// </summary>
[TestFixture]
public class MassTransitCommandTests
{
    [Test]
    public async Task SendsThroughTheBusAndCompletesWithinTheWindow()
    {
        using var host = await Start();

        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "mt-within"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Completed]));
    }

    [Test]
    public async Task ARemoteWorkerCompletesTheCommand()
    {
        using var host = await Start(window: TimeSpan.Zero);

        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "mt-pending"});

        Assert.That(receipts.Select(_ => _.Status), Is.EqualTo([CommandStatus.Pending, CommandStatus.Completed]));
    }

    [Test]
    public async Task CarriesTheHeaders()
    {
        using var host = await Start();
        var id = Guid.NewGuid();

        await ScryServer.Send(host.Services, "ShipParcel", new {label = "mt-headers"}, id, caller: "alice");

        Assert.That(Seen.For("mt-headers"), Is.EqualTo((id.ToString("D"), "alice")));
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

        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "mt-failing", fail = true});

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
        var dispatcher = host.Services.GetRequiredService<MassTransitDispatcher>();

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch(typeof(ShipParcel)), Is.True);
            Assert.That(dispatcher.CanDispatch(typeof(LocalChore)), Is.False);
        });
        var receipts = await ScryServer.Send(host.Services, "LocalChore", new { });
        Assert.That(receipts.Last().Status, Is.EqualTo(CommandStatus.Completed));
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
        ScryServer.Add(builder.Services, window, _ => _.UseMassTransitCommands(_ => _.For<ShipParcel>().For<WeighParcel>()), second);
        builder.Services.AddMassTransit(
            _ =>
            {
                _.AddScryCommandCompletions();
                _.AddConsumer<ShipParcelConsumer>();
                _.AddConsumer<WeighParcelConsumer>();
                _.UsingInMemory(
                    (context, bus) =>
                    {
                        bus.UseScryCommands(context);
                        bus.ConfigureEndpoints(context);
                    });
            });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }
}

public sealed class ShipParcelConsumer :
    IConsumer<ShipParcel>
{
    public Task Consume(ConsumeContext<ShipParcel> context)
    {
        Seen.Add(
            context.Message.Label,
            context.Headers.Get<string>(ScryCommandHeaders.CommandId),
            context.Headers.Get<string>(ScryCommandHeaders.Caller));
        if (context.Message.Fail)
        {
            throw new InvalidOperationException("The parcel could not be shipped.");
        }

        return Task.CompletedTask;
    }
}

public sealed class WeighParcelConsumer :
    IConsumer<WeighParcel>
{
    public Task Consume(ConsumeContext<WeighParcel> context)
    {
        context.SetScryResult(
            new Weighed
            {
                Grams = context.Message.Grams * 2
            });
        return Task.CompletedTask;
    }
}
