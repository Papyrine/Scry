/// <summary>
/// Commands in the sidecar: one row per command, whether it was seen on the wire, reported by the
/// client, or both — and a stream of receipts passed through rather than read.
/// </summary>
public class SidecarCommandTests
{
    static RenameThing Rename => new()
    {
        Id = 5,
        Name = "Renamed"
    };

    [Test]
    public async Task ACommandAnsweredAtOnceIsRecordedWhole()
    {
        var (store, client) = Watched(new(CommandStub.Json(CommandStatus.Completed)));

        await client.SendCommandAsync(Rename);

        var entry = store.Entries.Single();
        using (Assert.Multiple())
        {
            await Assert.That(entry.Kind).IsEqualTo(ScrySidecarKind.Command);
            await Assert.That(entry.Method).IsEqualTo("POST");
            await Assert.That(entry.RequestJson).Contains("\"command\": \"RenameThing\"");
            await Assert.That(entry.ResponseJson).Contains("Completed");
            await Assert.That(entry.Command!.Name).IsEqualTo("RenameThing");
            await Assert.That(entry.Command.State).IsEqualTo(ScryCommandActivityKind.Completed);
            await Assert.That(entry.Command.OnTheWire).IsTrue();
        }
    }

    // Read by the client above as it streams, so passed through; the row says pending until told more.
    [Test]
    public async Task ACommandAnsweredAsAStreamIsRecordedToItsHeaders()
    {
        await using var held = new HeldReceipts();
        var (store, client) = Watched(new(held.Step()), commandWait: TimeSpan.Zero);

        var sending = client.SendCommandAsync(Rename);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;

        var entry = store.Entries.Single();
        using (Assert.Multiple())
        {
            await Assert.That(entry.ResponseJson).IsNull();
            await Assert.That(entry.Status).IsEqualTo(200);
            await Assert.That(entry.Command!.State).IsEqualTo(ScryCommandActivityKind.Pending);
        }
    }

    // Asked for again by its id, a command stays one row, as a live query's reconnect does.
    [Test]
    public async Task AskingAgainFoldsIntoTheCommandsRow()
    {
        var (store, client) = Watched(
            new(
                CommandStub.Events(CommandStub.Result(CommandStatus.Pending)),
                CommandStub.Json(CommandStatus.Completed)));

        await client.SendCommandAsync(Rename);

        var entry = store.Entries.Single();
        using (Assert.Multiple())
        {
            await Assert.That(entry.Command!.Attempt).IsEqualTo(2);
            await Assert.That(entry.Command.State).IsEqualTo(ScryCommandActivityKind.Completed);
        }
    }

    [Test]
    public async Task TheCapabilitiesReadIsListedAsAboutNoOneCommand()
    {
        var (store, client) = Watched(new() {Capabilities = () => CommandStub.CapabilitiesOf("RenameThing")});

        await client.Ready;

        var entry = store.Entries.Single();
        using (Assert.Multiple())
        {
            await Assert.That(entry.Kind).IsEqualTo(ScrySidecarKind.Command);
            await Assert.That(entry.Command).IsNull();
            await Assert.That(entry.ResponseJson).Contains("RenameThing");
        }
    }

    [Test]
    public async Task ARefusedCommandSaysWhy()
    {
        var (store, client) = Watched(new(CommandStub.Refusal(HttpStatusCode.BadRequest, ScryErrorCode.Validation, "Unknown command 'RenameThing'.")));

        await Assert.ThrowsExactlyAsync<ScryRequestException>(() => client.SendCommandAsync(Rename));
        await Task.Yield();

        var command = store.Entries.Single().Command!;
        using (Assert.Multiple())
        {
            await Assert.That(command.State).IsEqualTo(ScryCommandActivityKind.Refused);
            await Assert.That(command.Error).IsEqualTo("Unknown command 'RenameThing'.");
        }
    }

    // Seen on the wire and reported by the client: one row, the wire's, carrying what the client said.
    [Test]
    public async Task AnObservedCommandIsOneRow()
    {
        var (store, client) = Watched(
            new(
                CommandStub.Events(CommandStub.Result(CommandStatus.Pending)),
                CommandStub.Json(CommandStatus.Completed, new {id = 3})));
        store.Observe(client);

        await client.SendCommandAsync(Rename);

        var entry = store.Entries.Single();
        using (Assert.Multiple())
        {
            await Assert.That(entry.Method).IsEqualTo("POST");
            await Assert.That(entry.Command!.OnTheWire).IsTrue();
            await Assert.That(entry.Command.State).IsEqualTo(ScryCommandActivityKind.Completed);
            await Assert.That(entry.Command.ResultJson).Contains("\"id\": 3");
        }
    }

    // Sent somewhere the sidecar cannot watch, a command is listed from what the client reports.
    [Test]
    public async Task ACommandSentOverACustomTransportIsListedFromItsReports()
    {
        var options = new ScrySidecarOptions();
        var store = new ScrySidecarStore(options);
        var client = new ScryClient(
            (_, _) => throw new NotSupportedException(),
            commandTransport: (request, _) => Receipts(CommandStub.Receipt(request.Id, CommandStatus.Completed)));
        store.Observe(client);

        await client.SendCommandAsync(Rename);

        var entry = store.Entries.Single();
        using (Assert.Multiple())
        {
            await Assert.That(entry.Kind).IsEqualTo(ScrySidecarKind.Command);
            await Assert.That(entry.Method).IsEqualTo("COMMAND");
            await Assert.That(entry.RequestJson).Contains("RenameThing");
            await Assert.That(entry.Command!.OnTheWire).IsFalse();
            await Assert.That(entry.Command.State).IsEqualTo(ScryCommandActivityKind.Completed);
        }
    }

    // With no exchange of its own to time, a reported command is timed from its send to its outcome
    // rather than listed as taking no time at all.
    [Test]
    public async Task AReportedCommandIsTimedToItsOutcome()
    {
        var options = new ScrySidecarOptions();
        var store = new ScrySidecarStore(options);
        var client = new ScryClient(
            (_, _) => throw new NotSupportedException(),
            commandTransport: (request, _) => Slowly(CommandStub.Receipt(request.Id, CommandStatus.Completed)));
        store.Observe(client);

        await client.SendCommandAsync(Rename);

        await Assert.That(store.Entries.Single().Duration).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
    }

    static async IAsyncEnumerable<CommandReceipt> Slowly(CommandReceipt receipt)
    {
        await Task.Delay(50);
        yield return receipt;
    }

    static async IAsyncEnumerable<CommandReceipt> Receipts(params CommandReceipt[] receipts)
    {
        foreach (var receipt in receipts)
        {
            yield return receipt;
        }

        await Task.CompletedTask;
    }

    static (ScrySidecarStore Store, ScryClient Client) Watched(CommandStub stub, TimeSpan? commandWait = null)
    {
        var options = new ScrySidecarOptions();
        var store = new ScrySidecarStore(options);
        var handler = new ScrySidecarHandler(store, options)
        {
            InnerHandler = stub.Handler()
        };
        var http = new HttpClient(handler)
        {
            BaseAddress = new("http://localhost")
        };
        var client = ScryClient.ForHttp(http, "/api/query");
        client.Reconnect = new Immediately();
        if (commandWait is { } wait)
        {
            client.CommandWait = wait;
        }

        return (store, client);
    }

    sealed class Immediately :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            TimeSpan.Zero;
    }
}
