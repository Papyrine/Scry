/// <summary>
/// What the client makes of a command's answers: which are outcomes and which are refusals, when it
/// stops waiting and lists the command as pending, how it asks again for one whose connection ended,
/// and what it calls a command it lost track of. Driven by scripted answers, so that what is pinned is
/// the client and not a server.
/// </summary>
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

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Completed);
            await Assert.That(outcome.Command).IsEqualTo("RenameThing");
            await Assert.That(outcome.Id).IsEqualTo(stub.LastId);
            await Assert.That(outcome.Completion.IsCompletedSuccessfully).IsTrue();
            await Assert.That(outcome.Pending).IsNull();
            await Assert.That(client.PendingWork.Items).IsEmpty();
            await Assert.That(stub.Requests).IsEquivalentTo(["POST /api/query/command"], CollectionOrdering.Matching);
        }
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
        using (Assert.Multiple())
        {
            await Assert.That(sent.Command).IsEqualTo("RenameThing");
            await Assert.That(sent.Version).IsEqualTo(CommandRequest.CurrentVersion);
            await Assert.That(sent.Stamp).IsEqualTo("client-stamp");
            await Assert.That(sent.Payload.GetRawText()).IsEqualTo("""{"id":5,"name":"Renamed"}""");
        }
    }

    [Test]
    public async Task AResultIsTyped()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Completed, new {id = 7}));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync<CreateThing, ThingCreated>(new() {Name = "New"});

        await Assert.That(outcome.Value.Id).IsEqualTo(7);
        await Assert.That((await outcome.Completion).Value.Id).IsEqualTo(7);
    }

    [Test]
    public async Task AFailedCommandIsAnOutcomeWithItsReason()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Failed, error: "The manager still has reports."));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Failed);
            await Assert.That(outcome.Error).IsEqualTo("The manager still has reports.");
        }
        var exception = Assert.ThrowsExactly<ScryCommandFailedException>(() => outcome.EnsureCompleted());
        await Assert.That(exception.Message).Contains("The manager still has reports.");
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

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Pending);
            await Assert.That(outcome.Pending).IsNotNull();
            await Assert.That(client.PendingWork.PendingCount).IsEqualTo(1);
            await Assert.That(client.PendingWork.Items.Single().Keys)
                .IsEquivalentTo<IReadOnlyList<string?>, string?>(["5"], CollectionOrdering.Matching);
            await Assert.That(client.PendingWork.Items.Single().Target).IsEqualTo("Thing");
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => outcome.EnsureCompleted());

        await held.Send(CommandStub.Result(CommandStatus.Completed));
        var final = await outcome.Completion.WaitAsync(patience);

        await Assert.That(final.Status).IsEqualTo(ScryCommandStatus.Completed);
        await Until(() => client.PendingWork.PendingCount == 0);
        await Assert.That(client.PendingWork.Items.Single().Status).IsEqualTo(ScryCommandStatus.Completed);
    }

    // Within the wait, a pending answer followed by the outcome is just the outcome.
    [Test]
    public async Task APendingAnswerFollowedByTheOutcomeWithinTheWaitIsTheOutcome()
    {
        var stub = new CommandStub(CommandStub.Events(CommandStub.Result(CommandStatus.Pending), CommandStub.Ping(), CommandStub.Result(CommandStatus.Completed)));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Completed);
            await Assert.That(client.PendingWork.Items).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Completed);
            await Assert.That(stub.Requests).IsEquivalentTo(["POST /api/query/command", $"GET /api/query/command/{outcome.Id:D}"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task AStreamTheServerEndedIsAskedForAgain()
    {
        var stub = new CommandStub(
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending), CommandStub.End()),
            CommandStub.Events(CommandStub.Result(CommandStatus.Pending), CommandStub.Result(CommandStatus.Completed)));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Completed);
        await Assert.That(stub.Requests).Count().IsEqualTo(2);
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

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Unknown);
            await Assert.That(outcome.Error).Contains("may have run");
        }
        Assert.ThrowsExactly<ScryCommandFailedException>(() => outcome.EnsureCompleted());
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

        await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Completed);
        await Assert.That(stub.Requests).Count().IsEqualTo(3);
    }

    [Test]
    public async Task ARetryPolicyThatGivesUpLeavesTheCommandUnknown()
    {
        var stub = new CommandStub(CommandStub.Events(CommandStub.Result(CommandStatus.Pending)));
        var client = stub.Client();
        client.Reconnect = new GivesUp();

        var outcome = await client.SendCommandAsync(Rename);

        await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Unknown);
        await Assert.That(stub.Requests).Count().IsEqualTo(1);
    }

    // A target that was gone by the time the command arrived is a race lost, not a mistake.
    [Test]
    public async Task AMissingTargetIsAFailedOutcome()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.NotFound, ScryErrorCode.NotFound, "The command's target was not found."));
        var client = stub.Client();

        var outcome = await client.SendCommandAsync(Rename);

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Failed);
            await Assert.That(outcome.Error).IsEqualTo("The command's target was not found.");
        }
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest, ScryErrorCode.Validation)]
    [Arguments(HttpStatusCode.BadRequest, ScryErrorCode.WireFormat)]
    [Arguments(HttpStatusCode.RequestEntityTooLarge, ScryErrorCode.PayloadTooLarge)]
    [Arguments(HttpStatusCode.ServiceUnavailable, ScryErrorCode.CommandLimit)]
    [Arguments(HttpStatusCode.TooManyRequests, ScryErrorCode.CommandLimit)]
    [Arguments(HttpStatusCode.InternalServerError, ScryErrorCode.ExecutionFailed)]
    public async Task ARefusalThrows(HttpStatusCode status, ScryErrorCode code)
    {
        var stub = new CommandStub(CommandStub.Refusal(status, code));
        var client = stub.Client();

        var exception = await Assert.ThrowsExactlyAsync<ScryRequestException>(() => client.SendCommandAsync(Rename));

        using (Assert.Multiple())
        {
            await Assert.That(exception!.Code).IsEqualTo(code);
            await Assert.That(exception.StatusCode).IsEqualTo(status);
            await Assert.That(client.PendingWork.Items).IsEmpty();
        }
    }

    [Test]
    public async Task AStaleClientIsToldSo()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.BadRequest, ScryErrorCode.StaleClient));

        await Assert.ThrowsExactlyAsync<ScryStaleClientException>(() => stub.Client().SendCommandAsync(Rename));
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
        await Assert.That(client.Can("RenameThing")).IsTrue();
        stub.Capabilities = () => CommandStub.CapabilitiesOf();

        await Assert.ThrowsExactlyAsync<ScryPermissionException>(() => client.SendCommandAsync(Rename));

        await Until(() => !client.Can("RenameThing"));
        await Assert.That(stub.CapabilityReads).IsEqualTo(2);
    }

    [Test]
    public async Task AServerNotServingCommandsIsSaidToBeOne()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.NotFound, code: null));

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => stub.Client().SendCommandAsync(Rename));

        await Assert.That(exception!.Message).Contains(nameof(ScryOptions.MaxPendingCommands));
    }

    // A single-page app's fallback route answers anything it does not know with its index page.
    [Test]
    public async Task SomebodyElsesAnswerIsNotACommandsAnswer()
    {
        var stub = new CommandStub(
            _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>", Encoding.UTF8, "text/html")
            });

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => stub.Client().SendCommandAsync(Rename));
    }

    [Test]
    public async Task AClassThatNamesNoCommandIsRefusedBeforeAnythingIsSent()
    {
        var stub = new CommandStub();

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => stub.Client().SendCommandAsync(new ThingCreated()));

        await Assert.That(exception!.Message).Contains("[ScryCommand]");
        await Assert.That(stub.Requests).IsEmpty();
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

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => sending);
        await Assert.That(exception!.CancellationToken).IsEqualTo(stopping.Token);
        await Assert.That(client.PendingWork.Items).IsEmpty();
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

        await Assert.ThrowsAsync<OperationCanceledException>(() => sending);
        var listed = client.PendingWork.Items.Single();
        await held.Send(CommandStub.Result(CommandStatus.Completed));

        await Assert.That((await listed.Completion.WaitAsync(patience)).Status).IsEqualTo(ScryCommandStatus.Completed);
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
        using (Assert.Multiple())
        {
            await Assert.That(final.Status).IsEqualTo(ScryCommandStatus.Unknown);
            await Assert.That(final.Error).Contains("disposed");
        }
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.SendCommandAsync(Rename));
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

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Value.Id).IsEqualTo(3);
            await Assert.That(reattached).IsEquivalentTo([outcome.Id], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ACustomTransportThatCannotAskAgainLeavesACutCommandUnknown()
    {
        var client = new ScryClient(
            (_, _) => throw new NotSupportedException(),
            commandTransport: (request, _) => Receipts(CommandStub.Receipt(request.Id, CommandStatus.Pending)));

        var outcome = await client.SendCommandAsync(Rename);

        await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Unknown);
    }

    [Test]
    public async Task AClientWithNoCommandTransportSaysSo()
    {
        var client = new ScryClient((_, _) => throw new NotSupportedException());

        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.SendCommandAsync(Rename));

        await Assert.That(exception!.Message).Contains("command transport");
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

        ScryCommandActivity[] snapshot;
        lock (reported)
        {
            snapshot = [.. reported];
        }

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.Select(_ => _.Kind)).IsEquivalentTo(
                [
                    ScryCommandActivityKind.Sent,
                    ScryCommandActivityKind.Pending,
                    ScryCommandActivityKind.Reattaching,
                    ScryCommandActivityKind.Completed
                ],
                CollectionOrdering.Matching);
            await Assert.That(snapshot.Select(_ => _.Id)).All(_ => Equals(_, outcome.Id));
            await Assert.That(snapshot.Select(_ => _.Attempt)).IsEquivalentTo([1, 1, 2, 2], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ARefusalIsReported()
    {
        var stub = new CommandStub(CommandStub.Refusal(HttpStatusCode.BadRequest, ScryErrorCode.Validation));
        var client = stub.Client();
        var reported = new List<ScryCommandActivityKind>();
        client.CommandActivity += _ => reported.Add(_.Kind);

        await Assert.ThrowsExactlyAsync<ScryRequestException>(() => client.SendCommandAsync(Rename));

        await Assert.That(reported).IsEquivalentTo([ScryCommandActivityKind.Sent, ScryCommandActivityKind.Refused], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AReportHandlerThatThrowsCostsNothing()
    {
        var stub = new CommandStub(CommandStub.Json(CommandStatus.Completed));
        var client = stub.Client();
        client.CommandActivity += _ => throw new InvalidOperationException("watching must not break sending");

        var outcome = await client.SendCommandAsync(Rename);

        await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Completed);
    }

    [Test]
    public void ANegativeWaitIsRefused() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CommandStub().Client().CommandWait = TimeSpan.FromSeconds(-1));

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
            await Assert.That(DateTime.UtcNow - started).IsLessThan(patience).Because("Waited too long.");
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
