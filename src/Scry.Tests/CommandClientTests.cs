/// <summary>
/// What the client makes of a command's answers: which are outcomes and which are refusals, when it
/// stops waiting and lists the command as pending, how it asks again for one whose connection ended,
/// and what it calls a command it lost track of. Driven by scripted answers, so that what is pinned is
/// the client and not a server.
/// </summary>
[TestFixture]
public class CommandClientTests
{
    static RenameThing Rename => new()
    {
        Id = 5,
        Name = "Renamed"
    };

    [Test]
    public async Task ACommandDecidedWithinTheWindowIsItsOutcome()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Completed));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
            Assert.That(outcome.Command, Is.EqualTo("RenameThing"));
            Assert.That(outcome.Id, Is.EqualTo(stub.LastId));
            Assert.That(outcome.Completion.IsCompletedSuccessfully, Is.True);
            Assert.That(outcome.Pending, Is.Null);
            Assert.That(client.PendingWork.Items, Is.Empty);
            Assert.That(stub.Requests, Is.EqualTo(["POST /api/query/command"]));
        });
    }

    // Camel-cased, as the server binds it, and the command named by its [ScryCommand] rather than its
    // class — which a hand-written class could name however it liked.
    [Test]
    public async Task TheCommandIsSentAsTheServerReadsIt()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Completed));
        var client = stub.Client();
        client.SchemaStamp = "client-stamp";

        await client.SendCommandAsync(Rename);

        var sent = stub.Sent.Single();
        Assert.Multiple(() =>
        {
            Assert.That(sent.Command, Is.EqualTo("RenameThing"));
            Assert.That(sent.Version, Is.EqualTo(CommandRequest.CurrentVersion));
            Assert.That(sent.Stamp, Is.EqualTo("client-stamp"));
            Assert.That(sent.Payload.GetRawText(), Is.EqualTo("""{"id":5,"name":"Renamed"}"""));
        });
    }

    [Test]
    public async Task AResultIsTyped()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Completed, new {id = 7}));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync<CreateThing, ThingCreated>(new() {Name = "New"});

        Assert.That(outcome.Value.Id, Is.EqualTo(7));
        Assert.That((await outcome.Completion).Value.Id, Is.EqualTo(7));
    }

    [Test]
    public async Task AFailedCommandIsAnOutcomeWithItsReason()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Failed, error: "The manager still has reports."));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Failed));
            Assert.That(outcome.Error, Is.EqualTo("The manager still has reports."));
        });
        var exception = Assert.Throws<ScryCommandFailedException>(() => outcome.EnsureCompleted());
        Assert.That(exception!.Message, Does.Contain("The manager still has reports."));
    }

    // Past the wait the command is the pending-work store's, and its outcome follows on the same stream.
    [Test]
    public async Task ACommandStillRunningPastTheWaitIsPendingAndFinishesLater()
    {
        await using var held = new HeldReceipts();
        var stub = new CommandStub(held.Step());
        var client = stub.Client(commandWait: TimeSpan.FromMilliseconds(100));
        client.PendingWork.CompletedLinger = TimeSpan.FromMinutes(1);

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        var outcome = await sending;

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Pending));
            Assert.That(outcome.Pending, Is.Not.Null);
            Assert.That(client.PendingWork.PendingCount, Is.EqualTo(1));
            Assert.That(client.PendingWork.Items.Single().Keys, Is.EqualTo(["5"]));
            Assert.That(client.PendingWork.Items.Single().Target, Is.EqualTo("Thing"));
        });
        Assert.Throws<InvalidOperationException>(() => outcome.EnsureCompleted());

        await held.Send(CommandStub.Result(CommandStatus.Completed));
        var final = await outcome.Completion.WaitAsync(patience);

        Assert.That(final.Status, Is.EqualTo(ScryCommandStatus.Completed));
        await Until(() => client.PendingWork.PendingCount == 0);
        Assert.That(client.PendingWork.Items.Single().Status, Is.EqualTo(ScryCommandStatus.Completed));
    }

    // Within the wait, a pending answer followed by the outcome is just the outcome.
    [Test]
    public async Task APendingAnswerFollowedByTheOutcomeWithinTheWaitIsTheOutcome()
    {
        var stub = new CommandStub(CommandStub.Events(CommandStub.Result(CommandStatus.Pending), CommandStub.Ping(), CommandStub.Result(CommandStatus.Completed)));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
            Assert.That(client.PendingWork.Items, Is.Empty);
        });
    }

    // A stream that stops before the outcome was cut: the command is asked for by its id.
    [Test]
    public async Task ACutStreamIsAskedForAgainByTheCommandsId()
    {
        var stub = new CommandStub(
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending)),
            CommandStub.Json(CommandStatus.Completed));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
            Assert.That(stub.Requests, Is.EqualTo(["POST /api/query/command", $"GET /api/query/command/{outcome.Id:D}"]));
        });
    }

    [Test]
    public async Task AStreamTheServerEndedIsAskedForAgain()
    {
        var stub = new CommandStub(
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending), CommandStub.End()),
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending), CommandStub.Result(CommandStatus.Completed)));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
        Assert.That(stub.Requests, Has.Count.EqualTo(2));
    }

    // Asked for again and not found: pruned, or asked of a node that never held it. It may have run.
    [Test]
    public async Task ACommandNotFoundWhenAskedForAgainIsUnknown()
    {
        var stub = new CommandStub(
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending)),
            CommandStub.Refusal(HttpStatusCode.NotFound, ScryErrorCode.NotFound, "The command is not one this server holds."));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Unknown));
            Assert.That(outcome.Error, Does.Contain("may have run"));
        });
        Assert.Throws<ScryCommandFailedException>(() => outcome.EnsureCompleted());
    }

    // A busy or failing server is asked again, as a live query's is.
    [Test]
    public async Task ABusyServerIsAskedAgain()
    {
        var stub = new CommandStub(
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending)),
            CommandStub.Refusal(HttpStatusCode.BadGateway, code: null),
            CommandStub.Json(CommandStatus.Completed));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
        Assert.That(stub.Requests, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ARetryPolicyThatGivesUpLeavesTheCommandUnknown()
    {
        var stub = new CommandStub(CommandStub.Events(CommandStub.Result(CommandStatus.Pending)));
        var client = stub.Client();
        client.Reconnect = new GivesUp();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Unknown));
        Assert.That(stub.Requests, Has.Count.EqualTo(1));
    }

    // A target that was gone by the time the command arrived is a race lost, not a mistake.
    [Test]
    public async Task AMissingTargetIsAFailedOutcome()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.NotFound, ScryErrorCode.NotFound, "The command's target was not found."));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Failed));
            Assert.That(outcome.Error, Is.EqualTo("The command's target was not found."));
        });
    }

    [TestCase(HttpStatusCode.BadRequest, ScryErrorCode.Validation)]
    [TestCase(HttpStatusCode.BadRequest, ScryErrorCode.WireFormat)]
    [TestCase(HttpStatusCode.RequestEntityTooLarge, ScryErrorCode.PayloadTooLarge)]
    [TestCase(HttpStatusCode.ServiceUnavailable, ScryErrorCode.CommandLimit)]
    [TestCase(HttpStatusCode.TooManyRequests, ScryErrorCode.CommandLimit)]
    [TestCase(HttpStatusCode.InternalServerError, ScryErrorCode.ExecutionFailed)]
    public void ARefusalThrows(HttpStatusCode status, ScryErrorCode code)
    {
        var stub = new CommandStub(CommandStub.Refusal(status, code));
        var client = stub.Client();

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => client.SendCommandAsync(Rename));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Code, Is.EqualTo(code));
            Assert.That(exception.StatusCode, Is.EqualTo(status));
            Assert.That(client.PendingWork.Items, Is.Empty);
        });
    }

    [Test]
    public void AStaleClientIsToldSo()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.BadRequest, ScryErrorCode.StaleClient));

        Assert.ThrowsAsync<ScryStaleClientException>(() => stub.Client().SendCommandAsync(Rename));
    }

    // Denied: what this caller may do moved since it was read, so it is read again.
    [Test]
    public async Task ADenialThrowsAndRereadsTheCapabilities()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.Forbidden, ScryErrorCode.Forbidden))
        {
            Capabilities = () => CommandStub.CapabilitiesOf("RenameThing")
        };
        var client = stub.Client();
        await client.Ready;
        Assert.That(client.Can("RenameThing"), Is.True);
        stub.Capabilities = () => CommandStub.CapabilitiesOf();

        Assert.ThrowsAsync<ScryPermissionException>(() => client.SendCommandAsync(Rename));

        await Until(() => !client.Can("RenameThing"));
        Assert.That(stub.CapabilityReads, Is.EqualTo(2));
    }

    [Test]
    public void AServerNotServingCommandsIsSaidToBeOne()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.NotFound, code: null));

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => stub.Client().SendCommandAsync(Rename));

        Assert.That(exception!.Message, Does.Contain(nameof(ScryOptions.MaxPendingCommands)));
    }

    // A single-page app's fallback route answers anything it does not know with its index page.
    [Test]
    public void SomebodyElsesAnswerIsNotACommandsAnswer()
    {
        var stub = new CommandStub(
            _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>", Encoding.UTF8, "text/html")
            });

        Assert.ThrowsAsync<NotSupportedException>(() => stub.Client().SendCommandAsync(Rename));
    }

    [Test]
    public void AClassThatNamesNoCommandIsRefusedBeforeAnythingIsSent()
    {
        var stub = new CommandStub();

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => stub.Client().SendCommandAsync(new ThingCreated()));

        Assert.That(exception!.Message, Does.Contain("[ScryCommand]"));
        Assert.That(stub.Requests, Is.Empty);
    }

    // Before the server has answered, stopping abandons the request; the command may not have arrived.
    [Test]
    public async Task StoppingBeforeTheAnswerAbandonsTheRequest()
    {
        await using var held = new HeldReceipts();
        var stub = new CommandStub(held.Step());
        var client = stub.Client();
        using var stopping = new CancelSource();

        var sending = client.SendCommandAsync(Rename, stopping.Token);
        await Until(() => stub.Requests.Count == 1);
        await stopping.CancelAsync();

        var exception = Assert.CatchAsync<OperationCanceledException>(() => sending);
        Assert.That(exception!.CancellationToken, Is.EqualTo(stopping.Token));
        Assert.That(client.PendingWork.Items, Is.Empty);
    }

    // After it, stopping stops the waiting and nothing else: the command is followed where anyone can see.
    [Test]
    public async Task StoppingAfterThePendingAnswerLeavesTheCommandInPendingWork()
    {
        await using var held = new HeldReceipts();
        var stub = new CommandStub(held.Step());
        var client = stub.Client(commandWait: Timeout.InfiniteTimeSpan);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CommandActivity += _ =>
        {
            if (_.Kind == ScryCommandActivityKind.Pending)
            {
                accepted.TrySetResult();
            }
        };
        // The token is the sender's, and ends with the sending: nothing after the cancel can use it.
        Task<ScryCommandOutcome> sending;
        using (var stopping = new CancelSource())
        {
            sending = client.SendCommandAsync(Rename, stopping.Token);
            await Until(() => stub.Requests.Count == 1);
            await held.Send(CommandStub.Result(CommandStatus.Pending));
            await accepted.Task.WaitAsync(patience, stopping.Token);
            await stopping.CancelAsync();
        }

        Assert.CatchAsync<OperationCanceledException>(() => sending);
        var listed = client.PendingWork.Items.Single();
        await held.Send(CommandStub.Result(CommandStatus.Completed));

        Assert.That((await listed.Completion.WaitAsync(patience)).Status, Is.EqualTo(ScryCommandStatus.Completed));
    }

    [Test]
    public async Task DisposingTheClientLeavesAPendingCommandUnknown()
    {
        await using var held = new HeldReceipts();
        var stub = new CommandStub(held.Step());
        var client = stub.Client(commandWait: TimeSpan.Zero);

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        var outcome = await sending;

        await client.DisposeAsync();

        var final = await outcome.Completion.WaitAsync(patience);
        Assert.Multiple(() =>
        {
            Assert.That(final.Status, Is.EqualTo(ScryCommandStatus.Unknown));
            Assert.That(final.Error, Does.Contain("disposed"));
        });
        Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendCommandAsync(Rename));
    }

    // A transport of the app's own: receipts as a sequence, a refusal as a throw, a cut as the
    // sequence running out.
    [Test]
    public async Task ACustomTransportCarriesCommands()
    {
        var reattached = new List<Guid>();
        var client = new ScryClient(
            (_, _) => throw new NotSupportedException(),
            commandTransport: (request, _) => Receipts(CommandStub.Receipt(request.Id, CommandStatus.Pending)),
            receiptTransport: (id, _) =>
            {
                reattached.Add(id);
                return Receipts(CommandStub.Receipt(id, CommandStatus.Completed, new {id = 3}));
            })
        {
            Reconnect = new Immediately()
        };

        var outcome = await client.SendCommandAsync<CreateThing, ThingCreated>(new() {Name = "New"});

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Value.Id, Is.EqualTo(3));
            Assert.That(reattached, Is.EqualTo([outcome.Id]));
        });
    }

    [Test]
    public async Task ACustomTransportThatCannotAskAgainLeavesACutCommandUnknown()
    {
        var client = new ScryClient(
            (_, _) => throw new NotSupportedException(),
            commandTransport: (request, _) => Receipts(CommandStub.Receipt(request.Id, CommandStatus.Pending)));

        var outcome = await client.SendCommandAsync(Rename);

        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Unknown));
    }

    [Test]
    public void AClientWithNoCommandTransportSaysSo()
    {
        var client = new ScryClient((_, _) => throw new NotSupportedException());

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => client.SendCommandAsync(Rename));

        Assert.That(exception!.Message, Does.Contain("command transport"));
    }

    // Every step a command takes is reported, over any transport, for the sidecar to list.
    [Test]
    public async Task EveryStepIsReported()
    {
        var stub = new CommandStub(
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending)),
            CommandStub.Json(CommandStatus.Completed));
        var client = stub.Client();
        var reported = new List<ScryCommandActivity>();
        client.CommandActivity += _ =>
        {
            lock (reported)
            {
                reported.Add(_);
            }
        };

        var outcome = await client.SendCommandAsync(Rename);

        lock (reported)
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    reported.Select(_ => _.Kind),
                    Is.EqualTo(
                    [
                        ScryCommandActivityKind.Sent,
                        ScryCommandActivityKind.Pending,
                        ScryCommandActivityKind.Reattaching,
                        ScryCommandActivityKind.Completed
                    ]));
                Assert.That(reported.Select(_ => _.Id), Is.All.EqualTo(outcome.Id));
                Assert.That(reported.Select(_ => _.Attempt), Is.EqualTo([1, 1, 2, 2]));
            });
        }
    }

    [Test]
    public void ARefusalIsReported()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.BadRequest, ScryErrorCode.Validation));
        var client = stub.Client();
        var reported = new List<ScryCommandActivityKind>();
        client.CommandActivity += _ => reported.Add(_.Kind);

        Assert.ThrowsAsync<ScryRequestException>(() => client.SendCommandAsync(Rename));

        Assert.That(reported, Is.EqualTo([ScryCommandActivityKind.Sent, ScryCommandActivityKind.Refused]));
    }

    [Test]
    public async Task AReportHandlerThatThrowsCostsNothing()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Completed));
        var client = stub.Client();
        client.CommandActivity += _ => throw new InvalidOperationException("watching must not break sending");

        var outcome = await client.SendCommandAsync(Rename);

        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
    }

    [Test]
    public void ANegativeWaitIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandStub().Client().CommandWait = TimeSpan.FromSeconds(-1));

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static async IAsyncEnumerable<CommandReceipt> Receipts(params CommandReceipt[] receipts)
    {
        foreach (var receipt in receipts)
        {
            yield return receipt;
        }

        await Task.CompletedTask;
    }

    static async Task Until(Func<bool> reached)
    {
        var started = DateTime.UtcNow;
        while (!reached())
        {
            Assert.That(DateTime.UtcNow - started, Is.LessThan(patience), "Waited too long.");
            await Task.Delay(10);
        }
    }

    sealed class GivesUp :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            null;
    }

    sealed class Immediately :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            TimeSpan.Zero;
    }
}
