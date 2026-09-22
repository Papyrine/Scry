/// <summary>
/// A command from the wire to its handler and back: bound into the server's class, authorized, its row
/// found through its policies, accepted, handled on a scope of its own, and answered with its outcome —
/// in one receipt where it finishes inside the sync window, and pending then final where it does not.
/// </summary>
[TestFixture]
public class CommandPipelineTests
{
    [Test]
    public async Task ACommandFinishingInsideTheWindowIsAnsweredWithItsOutcome()
    {
        await using var host = await CommandHost.Start("CommandInline");

        var receipts = await host.Send("CreateShift", new {name = "Late", day = "2026-03-05", perks = new[] {"Gym"}});

        Assert.That(receipts, Has.Count.EqualTo(1));
        var receipt = receipts[0];
        Assert.Multiple(() =>
        {
            Assert.That(receipt.Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(receipt.Result!.Value.GetProperty("id").GetInt32(), Is.GreaterThan(0));
            Assert.That(receipt.Stamp, Is.EqualTo(host.Processor.SchemaStamp));
        });
    }

    [Test]
    public async Task ASlowCommandIsAnsweredPendingThenWithItsOutcome()
    {
        await using var host = await CommandHost.Start("CommandPending", _ => _.CommandSyncWindow = TimeSpan.FromMilliseconds(50));
        host.Script.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reading = host.Database.NewDbContext();
        await using var scope = host.Services.CreateAsyncScope();

        await using var receipts = host.Processor
            .SendCommand(CommandHost.Request("RenameShift", new {id = 1, name = "Night"}), reading, scope.ServiceProvider, new HeaderDictionary())
            .GetAsyncEnumerator();

        Assert.That(await receipts.MoveNextAsync(), Is.True);
        Assert.That(receipts.Current.Status, Is.EqualTo(CommandStatus.Pending));

        host.Script.Gate.SetResult();

        Assert.That(await receipts.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        Assert.That(receipts.Current.Status, Is.EqualTo(CommandStatus.Completed));
        Assert.That(await receipts.MoveNextAsync(), Is.False);
        Assert.That(host.ShiftName(), Is.EqualTo("Night"));
    }

    // Saved after the handler returns, through the context it wrote to: a handler never has to.
    [Test]
    public async Task WhatAHandlerChangedIsSaved()
    {
        await using var host = await CommandHost.Start("CommandSaves");

        var receipts = await host.Send("RenameShift", new {id = 1, name = "Night"});

        Assert.Multiple(() =>
        {
            Assert.That(receipts.Single().Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(host.ShiftName(), Is.EqualTo("Night"));
        });
    }

    [Test]
    public async Task AHandlerMayTurnSavingOff()
    {
        await using var host = await CommandHost.Start("CommandSkipsSaving");
        host.Script.SkipSaving = true;

        var receipts = await host.Send("RenameShift", new {id = 1, name = "Night"});

        Assert.Multiple(() =>
        {
            Assert.That(receipts.Single().Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(host.ShiftName(), Is.EqualTo("Early"));
        });
    }

    [Test]
    public async Task AScryCommandExceptionIsShownToTheClient()
    {
        await using var host = await CommandHost.Start("CommandShowsItsFailure");
        host.Script.Throw = new ScryCommandException("That name is taken.");

        var receipt = (await host.Send("RenameShift", new {id = 1, name = "Night"})).Single();

        Assert.Multiple(() =>
        {
            Assert.That(receipt.Status, Is.EqualTo(CommandStatus.Failed));
            Assert.That(receipt.Error, Is.EqualTo("That name is taken."));
            Assert.That(host.ShiftName(), Is.EqualTo("Early"));
        });
    }

    // Anything else a handler throws is a developer's message, so the client is told only that it
    // failed — and the audit trail is where the real message is.
    [Test]
    public async Task AnyOtherFailureIsNotShownButIsAudited()
    {
        await using var host = await CommandHost.Start("CommandHidesItsFailure");
        host.Script.Throw = new InvalidOperationException("Connection string: Server=secret");

        var receipt = (await host.Send("RenameShift", new {id = 1, name = "Night"})).Single();

        Assert.Multiple(() =>
        {
            Assert.That(receipt.Status, Is.EqualTo(CommandStatus.Failed));
            Assert.That(receipt.Error, Is.EqualTo("Command execution failed."));
            Assert.That(host.Audited.Single().Error, Does.Contain("Server=secret"));
            Assert.That(host.Audited.Single().CommandStatus, Is.EqualTo(CommandStatus.Failed));
        });
    }

    [Test]
    public async Task TheHandlerIsHandedTheCallerAndTheTargetsKey()
    {
        await using var host = await CommandHost.Start("CommandContext");

        await host.Send("SealContract", new {contractId = 1}, caller: "alice");

        var (command, caller, keys) = host.Script.Seen.Single();
        Assert.Multiple(() =>
        {
            Assert.That(command, Is.EqualTo("SealContract"));
            Assert.That(caller, Is.EqualTo("alice"));
            Assert.That(keys, Is.EqualTo(new object[] {1}));
        });
    }

    [Test]
    public async Task AnUnknownCommandIsRejected()
    {
        await using var host = await CommandHost.Start("CommandUnknown");

        var exception = Assert.ThrowsAsync<ScryValidationException>(() => host.Send("Teleport", new { }));

        Assert.That(exception!.Message, Is.EqualTo("Unknown command 'Teleport'."));
    }

    // A property behind [CommandIgnore] is the server's to fill: a payload naming it is refused, by the
    // name the client used and no CLR type.
    [Test]
    public async Task APayloadNamingAPropertyTheCommandDoesNotExposeIsRejected()
    {
        await using var host = await CommandHost.Start("CommandIgnoredMember");

        var exception = Assert.ThrowsAsync<ScryValidationException>(() => host.Send("SealContract", new {contractId = 1, sealedBy = "mallory"}));

        Assert.That(exception!.Message, Is.EqualTo("The payload of command 'SealContract' carries 'sealedBy', which the command does not have."));
    }

    [Test]
    public async Task APayloadWithoutItsTargetsKeyIsRejected()
    {
        await using var host = await CommandHost.Start("CommandMissingKey");

        var exception = Assert.ThrowsAsync<ScryValidationException>(() => host.Send("SealContract", new { }));

        Assert.That(exception!.Message, Is.EqualTo("The payload of command 'SealContract' is missing 'contractId'."));
    }

    [Test]
    public async Task AnEnumTravelsByNameOnly()
    {
        await using var host = await CommandHost.Start("CommandEnumByNumber");

        var exception = Assert.ThrowsAsync<ScryValidationException>(() => host.Send("CreateShift", new {name = "Late", day = "2026-03-05", perks = new[] {1}}));

        Assert.That(exception!.Message, Does.StartWith("The payload of command 'CreateShift' is not valid at '$.perks"));
    }

    [Test]
    public async Task APayloadThatIsNotAnObjectIsRejected()
    {
        await using var host = await CommandHost.Start("CommandNotAnObject");

        var exception = Assert.ThrowsAsync<ScryValidationException>(() => host.Send("SealContract", new[] {1}));

        Assert.That(exception!.Message, Is.EqualTo("The payload of command 'SealContract' must be a JSON object."));
    }

    [Test]
    public async Task ACallerThePolicyRefusesOutrightIsDenied()
    {
        await using var host = await CommandHost.Start("CommandDenied", register: _ => _.AddSingleton(new CommandGate {Open = false}));

        var exception = Assert.ThrowsAsync<ScryPermissionException>(() => host.Send("SealContract", new {contractId = 1}));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo(ScryPermissionException.CommandDeniedMessage));
            Assert.That(host.Script.Seen, Is.Empty);
        });
    }

    // One query, one answer: a row the command's policy denies reads exactly as a row that is not there,
    // so a caller probing keys learns nothing about rows it may not act on.
    [Test]
    public async Task ARowDenialIsIndistinguishableFromAMissingRow()
    {
        await using var host = await CommandHost.Start("CommandRowDenied");

        var denied = Assert.ThrowsAsync<ScryCommandNotFoundException>(() => host.Send("SealContract", new {contractId = UnsealedContractsPolicy.SealedId}));
        var missing = Assert.ThrowsAsync<ScryCommandNotFoundException>(() => host.Send("SealContract", new {contractId = 999}));

        Assert.Multiple(() =>
        {
            Assert.That(denied!.Message, Is.EqualTo(ScryCommandNotFoundException.TargetMessage));
            Assert.That(missing!.Message, Is.EqualTo(denied.Message));
            Assert.That(host.Script.Seen, Is.Empty);
        });
    }

    [Test]
    public async Task ADuplicateIdIsRejected()
    {
        await using var host = await CommandHost.Start("CommandDuplicateId");
        var id = Guid.NewGuid();
        await host.Send("SealContract", new {contractId = 1}, id: id);

        var exception = Assert.ThrowsAsync<ScryValidationException>(() => host.Send("SealContract", new {contractId = 1}, id: id));

        Assert.That(exception!.Message, Does.Contain("has already been sent"));
    }

    [Test]
    public async Task TheServersLimitIsEnforced()
    {
        await using var host = await CommandHost.Start(
            "CommandServerLimit",
            _ =>
            {
                _.MaxPendingCommands = 1;
                _.CommandSyncWindow = TimeSpan.Zero;
            });
        host.Script.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That((await FirstReceipt(host, "SealContract", new {contractId = 1})).Status, Is.EqualTo(CommandStatus.Pending));

        var exception = Assert.ThrowsAsync<ScryCommandLimitException>(() => FirstReceipt(host, "SealContract", new {contractId = 1}));

        host.Script.Gate.SetResult();
        Assert.That(exception!.PerCaller, Is.False);
    }

    [Test]
    public async Task ACallersLimitIsTheirsAlone()
    {
        await using var host = await CommandHost.Start(
            "CommandCallerLimit",
            _ =>
            {
                _.MaxPendingCommandsPerCaller = 1;
                _.CommandSyncWindow = TimeSpan.Zero;
            });
        host.Script.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That((await FirstReceipt(host, "SealContract", new {contractId = 1}, "alice")).Status, Is.EqualTo(CommandStatus.Pending));

        var refused = Assert.ThrowsAsync<ScryCommandLimitException>(() => FirstReceipt(host, "SealContract", new {contractId = 1}, "alice"));
        var other = await FirstReceipt(host, "SealContract", new {contractId = 1}, "bob");

        host.Script.Gate.SetResult();
        Assert.Multiple(() =>
        {
            Assert.That(refused!.PerCaller, Is.True);
            Assert.That(other.Status, Is.EqualTo(CommandStatus.Pending));
        });
    }

    // Asked again by its id, a command answers its own caller — and nobody else, who is told exactly
    // what an id nobody sent is told.
    [Test]
    public async Task AReceiptIsGivenOnlyToItsCaller()
    {
        await using var host = await CommandHost.Start("CommandReceipt", _ => _.CommandSyncWindow = TimeSpan.Zero);
        host.Script.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid();
        Assert.That((await FirstReceipt(host, "SealContract", new {contractId = 1}, "alice", id)).Status, Is.EqualTo(CommandStatus.Pending));

        var stranger = Assert.ThrowsAsync<ScryCommandNotFoundException>(() => Receipts(host.Processor.Receipt(id, "bob")));
        var unknown = Assert.ThrowsAsync<ScryCommandNotFoundException>(() => Receipts(host.Processor.Receipt(Guid.NewGuid(), "alice")));

        host.Script.Gate.SetResult();
        var own = await Receipts(host.Processor.Receipt(id, "alice")).WaitAsync(patience);

        Assert.Multiple(() =>
        {
            Assert.That(stranger!.Message, Is.EqualTo(unknown!.Message));
            Assert.That(own.Last().Status, Is.EqualTo(CommandStatus.Completed));
        });
    }

    // The wait is the client's and the command is its handler's: a client that stops waiting has not
    // taken the command back.
    [Test]
    public async Task StoppingTheWaitDoesNotStopTheCommand()
    {
        await using var host = await CommandHost.Start("CommandAbandoned", _ => _.CommandSyncWindow = TimeSpan.Zero);
        host.Script.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid();
        using (var leaving = new CancelSource())
        {
            await using var reading = host.Database.NewDbContext();
            await using var scope = host.Services.CreateAsyncScope();
            await using var receipts = host.Processor
                .SendCommand(CommandHost.Request("RenameShift", new {id = 1, name = "Night"}, id), reading, scope.ServiceProvider, new HeaderDictionary(), cancel: leaving.Token)
                .GetAsyncEnumerator(leaving.Token);
            Assert.That(await receipts.MoveNextAsync(), Is.True);
            await leaving.CancelAsync();
        }

        host.Script.Gate.SetResult();
        var receipt = (await Receipts(host.Processor.Receipt(id)).WaitAsync(patience)).Last();

        Assert.Multiple(() =>
        {
            Assert.That(receipt.Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(host.ShiftName(), Is.EqualTo("Night"));
        });
    }

    [Test]
    public async Task ACompletedTargetedCommandNotifiesItsTarget()
    {
        await using var host = await CommandHost.Start("CommandNotifies");
        List<ScryChange> changes = [];
        using var listening = host.Processor.Changes.Listen(change =>
        {
            lock (changes)
            {
                changes.Add(change);
            }
        });

        // A handler that saves nothing: the notification is the pipeline's, not the interceptor's.
        await host.Send("SealContract", new {contractId = 1});

        lock (changes)
        {
            Assert.That(changes.SelectMany(_ => _.Entities), Does.Contain("Contract"));
        }
    }

    [Test]
    public async Task AFailedCommandNotifiesNothing()
    {
        await using var host = await CommandHost.Start("CommandFailsQuietly");
        host.Script.Throw = new ScryCommandException("No.");
        List<ScryChange> changes = [];
        using var listening = host.Processor.Changes.Listen(change =>
        {
            lock (changes)
            {
                changes.Add(change);
            }
        });

        await host.Send("SealContract", new {contractId = 1});

        lock (changes)
        {
            Assert.That(changes, Is.Empty);
        }
    }

    // One entry for a command answered with its outcome; two for one answered pending — the second when
    // it finishes, from a scope of its own.
    [Test]
    public async Task APendingCommandIsAuditedAgainWhenItFinishes()
    {
        await using var host = await CommandHost.Start("CommandAudited", _ => _.CommandSyncWindow = TimeSpan.Zero);
        host.Script.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = host.Send("RenameShift", new {id = 1, name = "Night"});
        await WaitFor(() => host.Audited.Count == 1);

        host.Script.Gate.SetResult();
        await sending.WaitAsync(patience);
        await WaitFor(() => host.Audited.Count == 2);

        Assert.Multiple(() =>
        {
            Assert.That(host.Audited[0].CommandStatus, Is.EqualTo(CommandStatus.Pending));
            Assert.That(host.Audited[1].CommandStatus, Is.EqualTo(CommandStatus.Completed));
            Assert.That(host.Audited[1].Command!.Command, Is.EqualTo("RenameShift"));
            Assert.That(host.Audited[1].Request, Is.Null);
        });
    }

    [Test]
    public async Task ARefusalIsAudited()
    {
        await using var host = await CommandHost.Start("CommandRefusalAudited");

        Assert.ThrowsAsync<ScryCommandNotFoundException>(() => host.Send("SealContract", new {contractId = 999}));

        var entry = host.Audited.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Outcome, Is.EqualTo(ScryQueryOutcome.Rejected));
            Assert.That(entry.CommandStatus, Is.Null);
            Assert.That(entry.Command!.Command, Is.EqualTo("SealContract"));
        });
    }

    [Test]
    public async Task CapabilitiesFollowTheCommandWidePolicy()
    {
        await using var host = await CommandHost.Start("CommandCapabilities");
        await using var reading = host.Database.NewDbContext();
        await using var closed = new ServiceCollection().AddSingleton(new CommandGate {Open = false}).BuildServiceProvider();

        var open = host.Processor.Capabilities(reading, host.Services, new HeaderDictionary());
        var shut = host.Processor.Capabilities(reading, closed, new HeaderDictionary());

        Assert.Multiple(() =>
        {
            Assert.That(open.Commands, Is.EqualTo(["CreateShift", "RenameShift", "SealContract"]));
            Assert.That(shut.Commands, Is.EqualTo(["CreateShift", "RenameShift"]));
            Assert.That(open.Stamp, Is.EqualTo(host.Processor.SchemaStamp));
        });
    }

    [Test]
    public void CommandsAreOffUntilAServerSaysHowManyItWillHold()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in SharedProcessor.Instance.SendCommand(CommandHost.Request("SealContract", new {contractId = 1}), context))
            {
            }
        });

        Assert.That(exception!.Message, Does.StartWith("Commands are off"));
    }

    // A command's save reaches a live query on the same processor through the interceptor on the
    // handler's context: nothing about the command is sent to it, only the rows it now reads.
    [Test]
    public async Task ALiveQuerySeesACommandsSave()
    {
        await using var host = await CommandHost.Start("CommandReachesALiveQuery");
        await using var answers = LiveShiftNames(host).GetAsyncEnumerator();
        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        Assert.That(answers.Current, Is.EqualTo(["Early"]));

        await host.Send("RenameShift", new {id = 1, name = "Night"});

        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        Assert.That(answers.Current, Is.EqualTo(["Night"]));
    }

    // A bulk write no interceptor can see still reaches it: a completed targeted command reports its
    // target, whatever its handler wrote with.
    [Test]
    public async Task ALiveQuerySeesABulkWriteThroughTheTargetNotice()
    {
        await using var host = await CommandHost.Start("CommandBulkReachesALiveQuery");
        host.Script.Bulk = true;
        await using var answers = LiveShiftNames(host).GetAsyncEnumerator();
        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);

        await host.Send("RenameShift", new {id = 1, name = "Night"});

        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        Assert.That(answers.Current, Is.EqualTo(["Night"]));
    }

    [Test]
    public async Task EveryCommandHasToGoSomewhere()
    {
        await using var host = await CommandHost.Start("CommandUnrouted");
        await using var empty = new ServiceCollection().BuildServiceProvider();

        var exception = Assert.Throws<Exception>(() => host.Processor.EnsureCommandsDispatchable(empty));

        Assert.That(exception!.Message, Does.StartWith("Command 'CreateShift' has nowhere to go: no dispatcher claims it and no ICommandHandler<CreateShift, ShiftCreated> is registered."));
    }

    [Test]
    public async Task ACommandTwoDispatchersClaimIsRefused()
    {
        await using var host = await CommandHost.Start(
            "CommandClaimedTwice",
            _ =>
            {
                _.AddDispatcher<ClaimsEverything>();
                _.AddDispatcher<AlsoClaimsEverything>();
            },
            _ => _.AddSingleton<ClaimsEverything>().AddSingleton<AlsoClaimsEverything>());

        var exception = Assert.Throws<Exception>(() => host.Processor.EnsureCommandsDispatchable(host.Services));

        Assert.That(exception!.Message, Does.Contain("is claimed by ClaimsEverything and AlsoClaimsEverything"));
    }

    // A dispatcher claims what it says it claims and reports the outcome back: the pipeline around it
    // is the same one an in-process handler runs in.
    [Test]
    public async Task ADispatcherCarriesTheCommandAndReportsItsOutcome()
    {
        await using var host = await CommandHost.Start(
            "CommandDispatched",
            _ => _.AddDispatcher<ClaimsEverything>(),
            _ => _.AddSingleton<ClaimsEverything>());
        var dispatcher = host.Services.GetRequiredService<ClaimsEverything>();
        dispatcher.Processor = host.Processor;

        var receipt = (await host.Send("SealContract", new {contractId = 1}, caller: "alice")).Single();

        Assert.Multiple(() =>
        {
            Assert.That(receipt.Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(host.Script.Seen, Is.Empty);
            Assert.That(dispatcher.Envelopes.Single().Caller, Is.EqualTo("alice"));
            Assert.That(dispatcher.Envelopes.Single().Keys, Is.EqualTo(new object[] {1}));
            Assert.That(((Seal) dispatcher.Envelopes.Single().Command).ContractId, Is.EqualTo(1));
        });
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static IAsyncEnumerable<IReadOnlyList<string>> LiveShiftNames(CommandHost host)
    {
        var reading = host.Database.NewDbContext();
        var client = new ScryClient(
            (request, _) => Task.FromResult(host.Processor.Execute(request, reading)),
            subscribeTransport: (request, cancel) => host.Processor.Subscribe(request, reading, cancel));
        return Names(client);
    }

    static async IAsyncEnumerable<IReadOnlyList<string>> Names(ScryClient client)
    {
        await foreach (var rows in client.Source<Shift>("Shift", ["Name"]).OrderBy(_ => _.Id).Select(_ => new {_.Name}).Live())
        {
            yield return [.. rows.Select(_ => _.Name)];
        }
    }

    static async Task<CommandReceipt> FirstReceipt(CommandHost host, string command, object payload, string? caller = null, Guid? id = null)
    {
        await using var reading = host.Database.NewDbContext();
        await using var scope = host.Services.CreateAsyncScope();
        await foreach (var receipt in host.Processor.SendCommand(CommandHost.Request(command, payload, id), reading, scope.ServiceProvider, new HeaderDictionary(), caller))
        {
            return receipt;
        }

        throw new("No receipt.");
    }

    static async Task<List<CommandReceipt>> Receipts(IAsyncEnumerable<CommandReceipt> receipts)
    {
        List<CommandReceipt> read = [];
        await foreach (var receipt in receipts)
        {
            read.Add(receipt);
        }

        return read;
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.That(Stopwatch.GetElapsedTime(started), Is.LessThan(patience));
            await Task.Delay(10);
        }
    }

    sealed class ClaimsEverything :
        ICommandDispatcher
    {
        public ScryProcessor? Processor { get; set; }

        public ConcurrentQueue<CommandEnvelope> Envelopes { get; } = new();

        public bool CanDispatch(Type commandType) =>
            true;

        public Task Dispatch(CommandEnvelope envelope, Cancel cancel)
        {
            Envelopes.Enqueue(envelope);
            Processor?.CompleteCommand(envelope.Id);
            return Task.CompletedTask;
        }
    }

    sealed class AlsoClaimsEverything :
        ICommandDispatcher
    {
        public bool CanDispatch(Type commandType) =>
            true;

        public Task Dispatch(CommandEnvelope envelope, Cancel cancel) =>
            Task.CompletedTask;
    }
}
