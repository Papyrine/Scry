/// <summary>
/// What host code sends outside Scry, recorded through <see cref="ScryExternalDisclosures"/>: kept in
/// the same record as the answers Scry sent, found by the same row, erased with it, and withdrawn
/// where the send did not happen.
/// </summary>
public class DisclosureExternalTests
{
    const string recipient = "payroll@example.test";

    static ScryDisclosureOptions Settings() =>
        new()
        {
            Node = "worker-1",
            Clock = new SteppingClock()
        };

    static ScryExternalDisclosure Payslip(int employee, string text) =>
        new()
        {
            Recipient = recipient,
            Agent = "PayslipHandler",
            Correlation = "command-7",
            ContentType = "text/plain",
            Sensitive = true,
            Units =
            [
                new()
                {
                    Content = Encoding.UTF8.GetBytes(text),
                    Rows = [new("Employee", [employee])]
                }
            ]
        };

    // A row a query sent and a handler then mailed is one row with two receivers: the record does
    // not care which of them Scry saw leave.
    [Test]
    public async Task ARowSentByAHandlerIsFoundBesideTheAnswersThatSentIt()
    {
        var (processor, store) = Disclosures.Audited();
        await Disclosures.Buffered(processor, Disclosures.Names());
        var answered = (await Disclosures.Events(store)).Single();
        var root = (await store.Reconstruct(answered.Event.Id))!.Units[0].Entities[0];
        var id = int.Parse(root.Key.Trim('[', ']'), CultureInfo.InvariantCulture);

        var external = new ScryExternalDisclosures(store, Settings());
        var record = await external.RecordAsync(Payslip(id, "Your payslip for March."));

        var receivers = await store.ReceiversOf("Employee", [id]).ToListAsync();
        var mailed = (await store.Reconstruct(record.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(receivers.Select(_ => _.Event.Caller ?? "")).IsEquivalentTo([Disclosures.Caller, recipient]);
            await Assert.That(mailed.Event.Kind).IsEqualTo(ScryDisclosureKind.External);
            await Assert.That(mailed.Event.Source).IsEqualTo("Employee");
            await Assert.That(mailed.Event.Agent).IsEqualTo("PayslipHandler");
            await Assert.That(mailed.Event.Correlation).IsEqualTo("command-7");
            await Assert.That(mailed.Event.ContentType).IsEqualTo("text/plain");
            await Assert.That(mailed.Event.Sensitive).IsTrue();
            await Assert.That(mailed.Event.Node).IsEqualTo("worker-1");
            await Assert.That(mailed.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Released);
            await Assert.That(mailed.Close.Units).IsEqualTo(1);
            await Assert.That(mailed.Close.Response).IsNotNull();
            await Assert.That(mailed.Units.Single().Content.Kind).IsEqualTo(ScryDisclosureContentKind.External);
            await Assert.That(Encoding.UTF8.GetString(mailed.Units.Single().Content.Bytes.Span)).IsEqualTo("Your payslip for March.");
        }
    }

    // What a handler does: record, then send, and say so where the send failed.
    [Test]
    public async Task AHandlerRecordsThenSends()
    {
        var store = new ScryMemoryDisclosureStore();
        var disclosures = new ScryExternalDisclosures(store, Settings());
        var outbox = new List<string>();
        var employee = new Employee {Id = 2, Name = "Aaron"};

        // begin-snippet: externalDisclosure
        var body = $"Dear {employee.Name}, your payslip for March is attached.";
        var record = await disclosures.RecordAsync(
            new()
            {
                Recipient = "aaron@example.test",
                Agent = "PayslipHandler",
                ContentType = "text/plain",
                Units =
                [
                    new()
                    {
                        Content = Encoding.UTF8.GetBytes(body),
                        Rows = [new("Employee", [employee.Id])]
                    }
                ]
            });
        try
        {
            outbox.Add(body);
        }
        catch
        {
            await disclosures.WithdrawAsync(record);
            throw;
        }
        // end-snippet

        var received = (await store.ReceiversOf("Employee", [2]).ToListAsync()).Single();
        using (Assert.Multiple())
        {
            await Assert.That(outbox).Count().IsEqualTo(1);
            await Assert.That(received.Event.Caller).IsEqualTo("aaron@example.test");
        }
    }

    // A send that failed after its record was accepted is said to have failed, by a second close.
    [Test]
    public async Task ASendThatFailedIsWithdrawn()
    {
        var store = new ScryMemoryDisclosureStore();
        var external = new ScryExternalDisclosures(store, Settings());
        var record = await external.RecordAsync(Payslip(2, "Your payslip."));

        await external.WithdrawAsync(record);

        // Said once: a second word on it, whatever it says, changes nothing.
        await external.WithdrawAsync(record, sent: 1);

        var close = (await store.Reconstruct(record.Id))!.Close!;
        using (Assert.Multiple())
        {
            await Assert.That(close.Outcome).IsEqualTo(ScryDisclosureOutcome.Retracted);
            await Assert.That(close.Units).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ASendCutShortSaysHowMuchWent()
    {
        var store = new ScryMemoryDisclosureStore();
        var external = new ScryExternalDisclosures(store, Settings());
        var disclosure = new ScryExternalDisclosure
        {
            Recipient = recipient,
            Units =
            [
                new() {Content = "one"u8.ToArray(), Rows = [new("Employee", [1])]},
                new() {Content = "two"u8.ToArray(), Rows = [new("Employee", [2])]}
            ]
        };
        var record = await external.RecordAsync(disclosure);

        await external.WithdrawAsync(record, sent: 1);

        var close = (await store.Reconstruct(record.Id))!.Close!;
        using (Assert.Multiple())
        {
            await Assert.That(close.Outcome).IsEqualTo(ScryDisclosureOutcome.Truncated);
            await Assert.That(close.Units).IsEqualTo(1);
        }
    }

    // Erasing a row removes what a handler sent of it, as it removes what a query sent.
    [Test]
    public async Task ErasingARowRemovesWhatWasSentOfIt()
    {
        var store = new ScryMemoryDisclosureStore();
        var external = new ScryExternalDisclosures(store, Settings());
        var record = await external.RecordAsync(Payslip(2, "Your payslip."));

        var erasure = await store.EraseAsync("Employee", [2], by: "dana");

        var unit = (await store.Reconstruct(record.Id))!.Units.Single();
        using (Assert.Multiple())
        {
            await Assert.That(erasure.Units).IsEqualTo(1);
            await Assert.That(unit.Erased).IsTrue();
            await Assert.That(unit.Content.Held).IsFalse();
            await Assert.That(await store.ReceiversOf("Employee", [2]).ToListAsync()).Count().IsEqualTo(1);
        }
    }

    // A unit the host says not to keep is recorded by its digest and length, and the content stays
    // out of the store.
    [Test]
    public async Task AUnitNotKeptIsRecordedByDigest()
    {
        var store = new ScryMemoryDisclosureStore();
        var external = new ScryExternalDisclosures(store, Settings());
        var record = await external.RecordAsync(
            new()
            {
                Recipient = recipient,
                Units = [new() {Content = new byte[300], Kept = false}]
            });

        var answer = (await store.Reconstruct(record.Id))!;
        var unit = answer.Units.Single();
        using (Assert.Multiple())
        {
            await Assert.That(answer.Event.Source).IsEqualTo("");
            await Assert.That(unit.Content.Held).IsFalse();
            await Assert.That(unit.Content.Length).IsEqualTo(300);
            await Assert.That(unit.Entities).IsEmpty();
        }
    }

    // Under a key, the same content is the same address it would be for any other recorder there.
    [Test]
    public async Task TheSameContentIsKeptOnce()
    {
        var store = new ScryMemoryDisclosureStore();
        var settings = Settings();
        settings.AddressKey = [1, 2, 3];
        var external = new ScryExternalDisclosures(store, settings);

        var first = await external.RecordAsync(Payslip(2, "Same text."));
        var second = await external.RecordAsync(Payslip(3, "Same text."));

        var one = (await store.Reconstruct(first.Id))!.Units.Single().Content.Address;
        var two = (await store.Reconstruct(second.Id))!.Units.Single().Content.Address;
        await Assert.That(one).IsEqualTo(two);
    }

    // A sink that does not take the record means nothing is to be sent: the host is told so.
    [Test]
    public async Task ADisclosureTheSinkRefusesThrows()
    {
        var external = new ScryExternalDisclosures(new RefusingSink(), Settings());

        var refused = await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            () => external.RecordAsync(Payslip(2, "Your payslip.")).AsTask());

        await Assert.That(refused!.Message).IsEqualTo("The disclosure audit did not accept the record of this answer, so it was not sent: The store is down.");
    }

    [Test]
    public async Task ADisclosureToNobodyIsRefused()
    {
        var external = new ScryExternalDisclosures(new ScryMemoryDisclosureStore(), Settings());

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => external.RecordAsync(new() {Recipient = " ", Units = []}).AsTask());
    }

    [Test]
    public async Task ARowWithNoKeyIsRefused()
    {
        var external = new ScryExternalDisclosures(new ScryMemoryDisclosureStore(), Settings());

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => external.RecordAsync(
                new()
                {
                    Recipient = recipient,
                    Units = [new() {Content = "x"u8.ToArray(), Rows = [new("Employee", [])]}]
                }).AsTask());
    }

    // A host with no model registers the record and gets the recorder with it.
    [Test]
    public async Task AHostWithNoModelCanRecord()
    {
        var store = new ScryMemoryDisclosureStore();
        var services = new ServiceCollection();
        services.AddScryDisclosureRecord(store, _ => _.Node = "worker-1");
        await using var provider = services.BuildServiceProvider();

        var external = provider.GetRequiredService<ScryExternalDisclosures>();
        var record = await external.RecordAsync(Payslip(2, "Your payslip."));

        var answer = (await store.Reconstruct(record.Id))!;
        await Assert.That(answer.Event.Node).IsEqualTo("worker-1");
    }
}
