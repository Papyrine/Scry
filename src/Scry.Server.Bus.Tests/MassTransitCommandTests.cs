using MassTransit;

/// <summary>
/// Commands over MassTransit's in-memory transport, the server and its consumers on one bus: the
/// command published with its headers, consumed by an ordinary consumer, and finished on the server by
/// the completion the filter publishes.
/// </summary>
public class MassTransitCommandTests
{
    // One host for each window rather than one a test: a bus a test, all starting at once, is what a
    // starved runner spent its time on, until a command outlasted the 60 seconds Send waits.
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
        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "mt-within"});

        await Assert.That(receipts.Select(_ => _.Status)).IsEquivalentTo([CommandStatus.Completed], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARemoteWorkerCompletesTheCommand()
    {
        var receipts = await ScryServer.Send(pendingHost.Services, "ShipParcel", new {label = "mt-pending"});

        await Assert.That(receipts.Select(_ => _.Status)).IsEquivalentTo([CommandStatus.Pending, CommandStatus.Completed], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CarriesTheHeaders()
    {
        var id = Guid.NewGuid();

        await ScryServer.Send(host.Services, "ShipParcel", new {label = "mt-headers"}, id, caller: "alice");

        await Assert.That(Seen.For("mt-headers")).IsEqualTo((id.ToString("D"), "alice"));
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
        var receipts = await ScryServer.Send(host.Services, "ShipParcel", new {label = "mt-failing", fail = true});

        using (Assert.Multiple())
        {
            await Assert.That(receipts.Last().Status).IsEqualTo(CommandStatus.Failed);
            await Assert.That(receipts.Last().Error).IsEqualTo("Command execution failed.");
        }
    }

    // A failed attempt is the retry's to decide, so it is not reported: an attempt that fails and a
    // retry that succeeds is a completed command. The filter runs inside the retry whichever order the
    // two are configured in, which is why a failure is told by the fault MassTransit publishes once
    // nothing more will be tried, and not by the filter.
    [Test]
    public async Task AFailureARetryRecoversFromIsNotReported()
    {
        using var retrying = await Start(retry: true);

        var receipts = await ScryServer.Send(retrying.Services, "ShipParcel", new {label = "mt-flaky"});

        using (Assert.Multiple())
        {
            await Assert.That(ShipParcelConsumer.Attempts("mt-flaky")).IsEqualTo(2);
            await Assert.That(receipts.Last().Status).IsEqualTo(CommandStatus.Completed);
        }
    }

    // Configured the other way round, the same.
    [Test]
    public async Task AFailureARetryRecoversFromIsNotReportedWhicheverOrder()
    {
        using var retrying = await Start(retry: true, retryFirst: true);

        var receipts = await ScryServer.Send(retrying.Services, "ShipParcel", new {label = "mt-flaky-first"});

        using (Assert.Multiple())
        {
            await Assert.That(ShipParcelConsumer.Attempts("mt-flaky-first")).IsEqualTo(2);
            await Assert.That(receipts.Last().Status).IsEqualTo(CommandStatus.Completed);
        }
    }

    // Every attempt failing is a failed command, told once the retries are spent.
    [Test]
    public async Task AFailureNoRetryRecoversFromIsReportedOnceTheyAreSpent()
    {
        using var retrying = await Start(retry: true);

        var receipts = await ScryServer.Send(retrying.Services, "ShipParcel", new {label = "mt-failing-retried", fail = true});

        using (Assert.Multiple())
        {
            await Assert.That(ShipParcelConsumer.Attempts("mt-failing-retried")).IsEqualTo(3);
            await Assert.That(receipts.Last().Status).IsEqualTo(CommandStatus.Failed);
            await Assert.That(receipts.Last().Error).IsEqualTo("Command execution failed.");
        }
    }

    [Test]
    public async Task TheAdapterClaimsOnlyWhatItWasTold()
    {
        var dispatcher = host.Services.GetRequiredService<MassTransitDispatcher>();

        using (Assert.Multiple())
        {
            await Assert.That(dispatcher.CanDispatch(typeof(ShipParcel))).IsTrue();
            await Assert.That(dispatcher.CanDispatch(typeof(LocalChore))).IsFalse();
        }
        var receipts = await ScryServer.Send(host.Services, "LocalChore", new { });
        await Assert.That(receipts.Last().Status).IsEqualTo(CommandStatus.Completed);
    }

    [Test]
    public async Task TwoAdaptersClaimingOneCommandRefuseStartup()
    {
        using var second = await Start(second: true);

        var exception = Assert.ThrowsExactly<Exception>(() => ScryServer.EnsureDispatchable(second.Services));

        await Assert.That(exception.Message).Contains("claimed by");
    }

    static async Task<IHost> Start(TimeSpan? window = null, bool second = false, bool retry = false, bool retryFirst = false)
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
                        if (retryFirst)
                        {
                            bus.UseMessageRetry(_ => _.Immediate(2));
                        }

                        bus.UseScryCommands(context);
                        if (retry && !retryFirst)
                        {
                            bus.UseMessageRetry(_ => _.Immediate(2));
                        }

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
    static ConcurrentDictionary<string, int> attempts = new();

    public static int Attempts(string label) =>
        attempts.GetValueOrDefault(label);

    public Task Consume(ConsumeContext<ShipParcel> context)
    {
        Seen.Add(
            context.Message.Label,
            context.Headers.Get<string>(ScryCommandHeaders.CommandId),
            context.Headers.Get<string>(ScryCommandHeaders.Caller));
        var attempt = attempts.AddOrUpdate(context.Message.Label, 1, (_, count) => count + 1);
        if (context.Message.Fail)
        {
            throw new InvalidOperationException("The parcel could not be shipped.");
        }

        // Fails its first attempt only, for a retry to recover from.
        if (context.Message.Label.StartsWith("mt-flaky", StringComparison.Ordinal) &&
            attempt == 1)
        {
            throw new InvalidOperationException("The parcel was not ready.");
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
