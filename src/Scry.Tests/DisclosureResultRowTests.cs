/// <summary>
/// The rows a command handler says its result was made from: recorded on the receipt that carries the
/// result, beside the row the command was sent against, and found by who received them.
/// </summary>
public class DisclosureResultRowTests
{
    // An untargeted command's result names the rows its handler read: those are what the receipt
    // carried, so the caller it went to is among who received each of them.
    [Test]
    public async Task TheRowsAResultWasMadeFromAreRecordedOnItsReceipt()
    {
        var store = new ScryMemoryDisclosureStore();
        await using var host = await CommandHost.Start("DisclosureResultRows", _ => _.UseDisclosureAudit(store));
        host.Script.During = _ =>
        {
            // By type, named as the schema names it; by wire name, as a worker with no model would.
            _.Discloses<Contract>(1);
            _.Discloses(new ScryDisclosureRow("Employee", [2]) {Version = "\"7\""});
        };

        var sent = await host.Send("CreateShift", new {name = "Late", day = "2026-03-05"}, caller: "alice");

        var receipt = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.Single().Status).IsEqualTo(CommandStatus.Completed);
            await Assert.That(receipt.Units.Single().Entities.Select(Disclosures.Row)).IsEquivalentTo(
                ["Contract[1] via result", "Employee[2] via result"],
                CollectionOrdering.Matching);
            await Assert.That(receipt.Units.Single().Entities[1].Version).IsEqualTo("\"7\"");
            await Assert.That((await store.ReceiversOf("Contract", [1]).ToListAsync()).Single().Event.Caller).IsEqualTo("alice");
            await Assert.That((await store.ReceiversOf("Employee", [2]).ToListAsync()).Single().Via).IsEqualTo("result");
        }
    }

    // A targeted command's receipt names the row it was sent against first, and the result's after.
    [Test]
    public async Task ATargetedCommandsReceiptNamesItsTargetThenItsResult()
    {
        var store = new ScryMemoryDisclosureStore();
        await using var host = await CommandHost.Start("DisclosureTargetAndResult", _ => _.UseDisclosureAudit(store));
        host.Script.During = _ => _.Discloses<Contract>(1);

        await host.Send("RenameShift", new {id = 1, name = "Night"}, caller: "alice");

        var receipt = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!;
        await Assert.That(receipt.Units.Single().Entities.Select(Disclosures.Row)).IsEquivalentTo(
            ["Shift[1] via target", "Contract[1] via result"],
            CollectionOrdering.Matching);
    }

    // A row of a source left out of the record is not named, as a query's would not be. A command
    // against such a row whose result names one that is recorded is recorded, without its target.
    [Test]
    public async Task ARowOfASourceLeftOutIsNotNamed()
    {
        var store = new ScryMemoryDisclosureStore();
        await using var host = await CommandHost.Start(
            "DisclosureResultLeftOut",
            _ => _.UseDisclosureAudit(store, _ => _.Exclude<Shift>()));
        host.Script.During = _ =>
        {
            _.Discloses<Shift>(1);
            _.Discloses<Contract>(1);
        };

        await host.Send("RenameShift", new {id = 1, name = "Night"}, caller: "alice");

        var receipt = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(receipt.Event.Source).IsEqualTo("");
            await Assert.That(receipt.Units.Single().Entities.Select(Disclosures.Row)).IsEquivalentTo(["Contract[1] via result"]);
        }
    }

    // A receipt that carries no result names none of the result's rows: a pending receipt is told
    // before the handler has said anything, and a failed command answered with nothing.
    [Test]
    public async Task AFailedCommandsReceiptNamesNoResultRows()
    {
        var store = new ScryMemoryDisclosureStore();
        await using var host = await CommandHost.Start("DisclosureResultFailed", _ => _.UseDisclosureAudit(store));
        host.Script.During = _ => _.Discloses<Contract>(1);
        host.Script.Throw = new ScryCommandException("No.");

        var sent = await host.Send("RenameShift", new {id = 1, name = "Night"}, caller: "alice");

        var receipt = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.Single().Status).IsEqualTo(CommandStatus.Failed);
            await Assert.That(receipt.Units.Single().Entities.Select(Disclosures.Row)).IsEquivalentTo(["Shift[1] via target"]);
        }
    }

    // What a worker writes onto its completion message, and the server reads off it: the same key text
    // the record would make of the values themselves.
    [Test]
    public async Task RowsSurviveTheTextTheyCrossABusIn()
    {
        var id = new Guid("0198a000-0000-7000-8000-000000000009");
        ScryDisclosureRow[] rows =
        [
            new("Employee", [5]),
            new("Line", ["A", 7]) {Version = "3"},
            new("Document", [id])
        ];

        var text = ScryDisclosureRow.Serialize(rows);
        var read = ScryDisclosureRow.Parse(text);

        using (Assert.Multiple())
        {
            await Assert.That(read.Select(_ => _.Source)).IsEquivalentTo(["Employee", "Line", "Document"], CollectionOrdering.Matching);
            await Assert.That(read.Select(_ => ScryDisclosureEntity.KeyOf(_.Key))).IsEquivalentTo(
                rows.Select(_ => ScryDisclosureEntity.KeyOf(_.Key)),
                CollectionOrdering.Matching);
            await Assert.That(read.Select(_ => _.Version)).IsEquivalentTo([null, "3", null], CollectionOrdering.Matching);
            await Assert.That(ScryDisclosureRow.Serialize([])).IsNull();
            await Assert.That(ScryDisclosureRow.Parse(null)).IsEmpty();
        }
    }

    [Test]
    public async Task TextThatIsNotRowsIsRefused()
    {
        using (Assert.Multiple())
        {
            await Assert.That(() => ScryDisclosureRow.Parse("{}")).ThrowsExactly<FormatException>();
            await Assert.That(() => ScryDisclosureRow.Parse("[{\"source\":\"Employee\"}]")).ThrowsExactly<FormatException>();
            await Assert.That(() => ScryDisclosureRow.Parse("[{\"source\":\"Employee\",\"key\":[]}]")).ThrowsExactly<FormatException>();
            await Assert.That(() => ScryDisclosureRow.Parse("not json")).ThrowsExactly<FormatException>();
        }
    }

    [Test]
    public async Task ARowWithNoKeyIsRefusedWhenDeclared()
    {
        var context = new ScryCommandContext(EmptyServiceProvider.Instance, null!, Guid.NewGuid(), "Any", caller: null, []);

        await Assert.That(() => context.Discloses(new ScryDisclosureRow("Employee", []))).ThrowsExactly<ArgumentException>();
    }
}
