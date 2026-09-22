/// <summary>
/// Commands in the sidecar: one row per command, whether it was seen on the wire, reported by the
/// client, or both — and a stream of receipts passed through rather than read.
/// </summary>
[TestFixture]
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
        Assert.Multiple(() =>
        {
            Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Command));
            Assert.That(entry.Method, Is.EqualTo("POST"));
            Assert.That(entry.RequestJson, Does.Contain("\"command\": \"RenameThing\""));
            Assert.That(entry.ResponseJson, Does.Contain("Completed"));
            Assert.That(entry.Command!.Name, Is.EqualTo("RenameThing"));
            Assert.That(entry.Command.State, Is.EqualTo(ScryCommandActivityKind.Completed));
            Assert.That(entry.Command.OnTheWire, Is.True);
        });
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
        Assert.Multiple(() =>
        {
            Assert.That(entry.ResponseJson, Is.Null);
            Assert.That(entry.Status, Is.EqualTo(200));
            Assert.That(entry.Command!.State, Is.EqualTo(ScryCommandActivityKind.Pending));
        });
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
        Assert.Multiple(() =>
        {
            Assert.That(entry.Command!.Attempt, Is.EqualTo(2));
            Assert.That(entry.Command.State, Is.EqualTo(ScryCommandActivityKind.Completed));
        });
    }

    [Test]
    public async Task TheCapabilitiesReadIsListedAsAboutNoOneCommand()
    {
        var (store, client) = Watched(new() {Capabilities = () => CommandStub.CapabilitiesOf("RenameThing")});

        await client.Ready;

        var entry = store.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Command));
            Assert.That(entry.Command, Is.Null);
            Assert.That(entry.ResponseJson, Does.Contain("RenameThing"));
        });
    }

    [Test]
    public async Task ARefusedCommandSaysWhy()
    {
        var (store, client) = Watched(new(CommandStub.Refusal(HttpStatusCode.BadRequest, ScryErrorCode.Validation, "Unknown command 'RenameThing'.")));

        Assert.ThrowsAsync<ScryRequestException>(() => client.SendCommandAsync(Rename));
        await Task.Yield();

        var command = store.Entries.Single().Command!;
        Assert.Multiple(() =>
        {
            Assert.That(command.State, Is.EqualTo(ScryCommandActivityKind.Refused));
            Assert.That(command.Error, Is.EqualTo("Unknown command 'RenameThing'."));
        });
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
        Assert.Multiple(() =>
        {
            Assert.That(entry.Method, Is.EqualTo("POST"));
            Assert.That(entry.Command!.OnTheWire, Is.True);
            Assert.That(entry.Command.State, Is.EqualTo(ScryCommandActivityKind.Completed));
            Assert.That(entry.Command.ResultJson, Does.Contain("\"id\": 3"));
        });
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
        Assert.Multiple(() =>
        {
            Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Command));
            Assert.That(entry.Method, Is.EqualTo("COMMAND"));
            Assert.That(entry.RequestJson, Does.Contain("RenameThing"));
            Assert.That(entry.Command!.OnTheWire, Is.False);
            Assert.That(entry.Command.State, Is.EqualTo(ScryCommandActivityKind.Completed));
        });
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

        Assert.That(store.Entries.Single().Duration, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40)));
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
