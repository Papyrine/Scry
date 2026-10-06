// The disclosure explorer, driven through a real browser against the real Sample.DisclosureServer:
// answers are asked for as one caller or another, and the record of them is then read as the reviewer.
// What each answer to a question holds is asserted without a browser in Scry.Server.Disclosure.Tests;
// these are what only a browser can say — that the page boots under its policy, that a question is an
// address, that what is on screen is what was recorded.
//
// The tests share one server and so one record, and run side by side. Each asks as callers of its
// own, and asserts about those, so that none of them is counting on what another did or did not do.
[Category("Browser")]
public class DisclosureUiTests :
    DisclosureFixture
{
    // For what waits on the record itself rather than on the page: an erasure takes the lock every
    // writer of the record takes, and a check reads every link. Both are quick, and both are behind
    // whatever else the database is doing — which, with several servers starting at once, can be a lot.
    static LocatorAssertionsToContainTextOptions patient = new()
    {
        Timeout = 30_000
    };

    // A link to a question opens that question: the form shows what is being asked, and the answer
    // says who was sent the row, which members went with it, and flags the one the model marks sensitive.
    [Test]
    public async Task ALinkAsksWhoReceivedARow()
    {
        var page = await SignedIn("ann.rows");
        await Assert.That(await Ask(page, Names)).IsEqualTo(200);
        await Assert.That(await Ask(page, Passwords)).IsEqualTo(200);

        await Become(page, Reviewer);
        await Explore(page, "row?source=Employee&key=1");
        await Answered(page, "row-results");

        var sent = page.Locator("[data-testid='row-table'] tbody tr").Filter(new() {HasText = "ann.rows"});
        await Assertions.Expect(sent).ToHaveCountAsync(2);
        await Assertions.Expect(sent.Locator(".chip.sensitive")).ToHaveTextAsync("Employee.Password");
        await Assertions.Expect(page.Locator("[data-testid='row-heading']")).ToHaveTextAsync("Employee[1]");
        await Assertions.Expect(page.Locator("[data-testid='row-source']")).ToHaveValueAsync("Employee");
        await Assertions.Expect(page.Locator("[data-testid='row-key']")).ToHaveValueAsync("1");
        await Assertions.Expect(page.Locator("[data-testid='reviewer']")).ToHaveTextAsync("Reading as auditor");
        await Assertions.Expect(page.Locator("[data-testid='title']")).ToHaveTextAsync("Who received a row");
    }

    // Asked from the form, a question becomes the page's address — so it can be linked to, and Back
    // returns to the one before it.
    [Test]
    public async Task AQuestionAskedIsAnAddress()
    {
        var page = await SignedIn("ben.address");
        await Ask(page, Names);

        await Become(page, Reviewer);
        await Explore(page, "row");
        await page.Locator("[data-testid='row-source']").SelectOptionAsync("Employee");
        await page.Locator("[data-testid='row-key']").FillAsync("2");
        await page.Locator("[data-testid='row-ask']").ClickAsync();
        await Answered(page, "row-results");

        await Assert.That(new Uri(page.Url).Fragment).IsEqualTo("#row?key=2&source=Employee");
        await Assertions.Expect(page.Locator("[data-testid='row-heading']")).ToHaveTextAsync("Employee[2]");

        // The rail starts another question, and Back takes the page to the one it was asking.
        await page.Locator("[data-testid='rail-caller']").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='title']")).ToHaveTextAsync("What a caller received");
        await page.GoBackAsync();
        await Answered(page, "row-results");
        await Assertions.Expect(page.Locator("[data-testid='row-heading']")).ToHaveTextAsync("Employee[2]");
    }

    // Opening an answer shows what it carried — and is recorded: back in the list, the reviewer is
    // now among those who received the row.
    [Test]
    public async Task OpeningAnAnswerShowsWhatItCarriedAndRecordsTheReader()
    {
        var page = await SignedIn("cat.opens");
        await Ask(page, Passwords);

        await Become(page, Reviewer);
        await Explore(page, "row?source=Employee&key=1");
        await Answered(page, "row-results");
        var sent = page.Locator("[data-testid='row-table'] tbody tr").Filter(new() {HasText = "cat.opens"});
        await Assertions.Expect(sent).ToHaveCountAsync(1);
        await sent.Locator("[data-testid='open-event']").ClickAsync();
        await Answered(page, "event-view");

        await Assertions.Expect(page.Locator("[data-testid='title']")).ToHaveTextAsync("One answer");
        await Assertions.Expect(page.Locator("[data-testid='event-caller']")).ToHaveTextAsync("cat.opens");
        await Assertions.Expect(page.Locator("[data-testid='event-kind']")).ToHaveTextAsync("List of Employee");
        await Assertions.Expect(page.Locator("[data-testid='event-outcome']")).ToContainTextAsync("Released, with 2 units sent");
        await Assertions.Expect(page.Locator("[data-testid='event-payload']")).ToContainTextAsync("\"name\": \"Alice\"");
        await Assertions.Expect(page.Locator("[data-testid='event-request']")).ToContainTextAsync("\"root\": \"Employee\"");
        await Assertions.Expect(page.Locator("[data-testid='event-fields'] tbody tr").Filter(new() {HasText = "Employee.Password"})).ToContainTextAsync("sensitive");
        await Assertions.Expect(page.Locator("[data-testid='event-recorded']")).ToContainTextAsync("auditor is now among those who received its rows");

        // The unit that carried Alice names her row, and the department it was read through.
        var alice = page.Locator("[data-testid='event-units'] tbody tr").Filter(new() {HasText = "\"name\":\"Alice\""});
        await Assertions.Expect(alice.Locator("[data-testid='unit-row']")).ToHaveTextAsync(["Employee[1]", "Department[1]"]);

        // That the answer was opened is in the record before it was shown; the list reads it once
        // the record has moved it on.
        await Host.Settled();
        await page.GoBackAsync();
        await Answered(page, "row-results");
        var shown = page.Locator("[data-testid='row-table'] tbody tr.reviewed").Filter(new() {HasText = "the answer sent to cat.opens"});
        await Assertions.Expect(shown).ToHaveCountAsync(1);
        await Assertions.Expect(shown).ToContainTextAsync("auditor, reading the record");
    }

    // What a caller received, asked from the form: every answer sent to them, newest first, with how
    // each ended and none of what any of them carried.
    [Test]
    public async Task WhatACallerReceived()
    {
        var page = await SignedIn("dee.caller");
        await Ask(page, Names);
        await Ask(page, Passwords);
        await Ask(page, Count);

        await Become(page, Reviewer);
        await Explore(page, "caller");
        await page.Locator("[data-testid='caller-name']").FillAsync("dee.caller");
        await page.Locator("[data-testid='caller-ask']").ClickAsync();
        await Answered(page, "events");

        var answers = page.Locator("[data-testid='events-table'] tbody tr");
        await Assertions.Expect(answers).ToHaveCountAsync(3);
        await Assertions.Expect(answers.Nth(0)).ToContainTextAsync("Scalar");
        await Assertions.Expect(answers.Nth(1).Locator(".chip.sensitive")).ToHaveTextAsync("sensitive");
        await Assertions.Expect(answers.Nth(2)).ToContainTextAsync("4 units");
        await Assertions.Expect(page.Locator("[data-testid='events-heading']")).ToHaveTextAsync("Sent to dee.caller");
        await Assertions.Expect(page.Locator("[data-testid='events']")).Not.ToContainTextAsync("Alice");

        // A time is typed one way, the way that reads the same wherever it is read, and is UTC. Typed
        // any other way it is the field that says so, and the question waits.
        var from = page.Locator("[data-testid='caller-from']");
        var ask = page.Locator("[data-testid='caller-ask']");
        await from.FillAsync("01/03/2026");
        await Assertions.Expect(from).ToHaveClassAsync("mistyped");
        await Assertions.Expect(ask).ToBeDisabledAsync();

        // In a range of time nothing was sent in, nothing was sent.
        await from.FillAsync("2031-01-01 00:00");
        await ask.ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='events-none']")).ToContainTextAsync("no answer sent to dee.caller in that time");
        await Assert.That(new Uri(page.Url).Fragment).IsEqualTo("#caller?caller=dee.caller&from=2031-01-01T00%3A00%3A00");

        // And in one that covers it, everything was.
        await from.FillAsync("2026-01-01");
        await ask.ClickAsync();
        await Assertions.Expect(answers).ToHaveCountAsync(3);

        // Somebody the record has never heard of was sent nothing, and the page says so.
        await page.Locator("[data-testid='caller-name']").FillAsync("nobody.at.all");
        await ask.ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='events-none']")).ToContainTextAsync("no answer sent to nobody.at.all");
    }

    // Whether a caller was ever sent a member: a yes or a no before it is a list. One caller asked
    // for the sensitive member and the other did not.
    [Test]
    public async Task WhetherACallerReceivedAMember()
    {
        var page = await SignedIn("eve.member");
        await Ask(page, Passwords);
        await Become(page, "fay.member");
        await Ask(page, Names);

        await Become(page, Reviewer);
        await Explore(page, "member");
        await page.Locator("[data-testid='member-caller']").FillAsync("eve.member");
        await page.Locator("[data-testid='member-source']").SelectOptionAsync("Employee");
        await page.Locator("[data-testid='member-name']").SelectOptionAsync("Password");
        await page.Locator("[data-testid='member-ask']").ClickAsync();
        await Answered(page, "events");
        await Assertions.Expect(page.Locator("[data-testid='verdict']")).ToContainTextAsync("Yes. eve.member was sent Employee.Password");
        await Assertions.Expect(page.Locator("[data-testid='events-table'] tbody tr")).ToHaveCountAsync(1);

        await page.Locator("[data-testid='member-caller']").FillAsync("fay.member");
        await page.Locator("[data-testid='member-ask']").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='verdict']")).ToHaveTextAsync("No. The record does not say fay.member was ever sent Employee.Password.");

        // The member the model marks sensitive is offered as one.
        await Assertions.Expect(page.Locator("[data-testid='member-name'] option").Filter(new() {HasText = "Password"})).ToHaveTextAsync("Password (sensitive)");
    }

    // How the record stands: what it holds, that nothing is waiting, that its chain still follows —
    // and who has been reading it, which by now includes whoever is looking.
    [Test]
    public async Task TheRecordSaysHowItStands()
    {
        var page = await SignedIn("gus.status");
        await Ask(page, Names);

        await Become(page, Reviewer);
        await Explore(page, "caller?caller=gus.status");
        await Answered(page, "events");
        await page.Locator("[data-testid='rail-status']").ClickAsync();
        await Answered(page, "status-view");

        await Assertions.Expect(page.Locator("[data-testid='title']")).ToHaveTextAsync("The record");
        await Assertions.Expect(page.Locator("[data-testid='status-events']")).Not.ToHaveTextAsync("0");
        await Assertions.Expect(page.Locator("[data-testid='reviews'] tbody tr").Filter(new() {HasText = "What a caller received"}).First).ToContainTextAsync("auditor");

        await page.Locator("[data-testid='verify']").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='status-check']")).ToContainTextAsync("links, each following from the one before", patient);
    }

    // Erasing a row's content takes the key typed again, and removes what was sent of it while the
    // record of the sending stays. Erasure takes every piece of content the row was ever sent in,
    // whoever it was sent to, so the row here is one no other test is sent: the second department,
    // which nothing else asks for by name and no other query reaches.
    [Test]
    public async Task ErasingARowRemovesWhatWasSentOfIt()
    {
        const string departments =
            """{"version":1,"root":"Department","pipeline":[{"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},{"$type":"select","projection":{"members":["Name"]}}]}""";
        var page = await SignedIn("hal.erase");
        await Ask(page, departments);

        await Become(page, Reviewer);
        await Explore(page, "row?source=Department&key=2");
        await Answered(page, "row-results");
        var sent = page.Locator("[data-testid='row-table'] tbody tr").Filter(new() {HasText = "hal.erase"});
        await Assertions.Expect(sent).ToHaveCountAsync(1);

        // Not until the key has been typed again.
        var erase = page.Locator("[data-testid='erase-button']");
        await Assertions.Expect(erase).ToBeDisabledAsync();
        await page.Locator("[data-testid='erase-confirm']").FillAsync("5");
        await erase.ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='erase-error']")).ToContainTextAsync("The key typed again is not the key. Nothing was erased.", patient);

        await page.Locator("[data-testid='erase-confirm']").FillAsync("2");
        await erase.ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='erase-done']")).ToContainTextAsync("Erased 1 piece of content", patient);

        // That it was sent is still there to be asked about; what it was is gone.
        await Assertions.Expect(sent).ToHaveCountAsync(1);
        await sent.Locator("[data-testid='open-event']").ClickAsync();
        await Answered(page, "event-view");
        var units = page.Locator("[data-testid='event-units'] tbody tr");
        await Assertions.Expect(units.Filter(new() {HasText = "Department[2]"})).ToContainTextAsync("(erased, was");
        await Assertions.Expect(units.Filter(new() {HasText = "Department[1]"})).ToContainTextAsync("\"name\":\"Engineering\"");
        await Assertions.Expect(page.Locator("[data-testid='event-payload']")).ToContainTextAsync("\"$erased\": true");
    }

    // An export is a file: its name, and what it holds, are what the page hands the browser to save.
    [Test]
    public async Task AResultIsExportedAsAFile()
    {
        var page = await SignedIn("ivy.export");
        await Ask(page, Names);

        await Become(page, Reviewer);
        await Explore(page, "caller?caller=ivy.export");
        await Answered(page, "events");

        // In place of the click a browser would turn into a save.
        await page.EvaluateAsync("() => { window.saved = []; window.disclosure.save = async (name, blob) => window.saved.push({ name, type: blob.type, text: await blob.text() }); }");
        await page.Locator("[data-testid='export-csv']").ClickAsync();
        await page.WaitForFunctionAsync("() => window.saved.length === 1");
        await page.Locator("[data-testid='export-json']").ClickAsync();
        await page.WaitForFunctionAsync("() => window.saved.length === 2");

        var csv = await page.EvaluateAsync<JsonElement>("() => window.saved[0]");
        var json = await page.EvaluateAsync<JsonElement>("() => window.saved[1]");
        var lines = csv.GetProperty("text").GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var document = JsonDocument.Parse(json.GetProperty("text").GetString()!);
        using (Assert.Multiple())
        {
            await Assert.That(csv.GetProperty("name").GetString()!).Matches("^disclosures-\\d{8}-\\d{6}\\.csv$");
            await Assert.That(csv.GetProperty("type").GetString()).IsEqualTo("text/csv");
            await Assert.That(lines[0].TrimStart('﻿')).IsEqualTo("When,Caller,Kind,Source,Event,Outcome,Unit,Released,Rows,Content");
            await Assert.That(lines).Count().IsEqualTo(5);
            await Assert.That(lines.Skip(1).All(_ => _.Contains(",ivy.export,List,Employee,"))).IsTrue();
            await Assert.That(lines[2]).Contains("Employee[1],\"{\"\"name\"\":\"\"Alice\"\"}\"");
            await Assert.That(json.GetProperty("name").GetString()!).EndsWith(".json");
            await Assert.That(document.RootElement.GetProperty("events")[0].GetProperty("units").GetArrayLength()).IsEqualTo(4);
        }
    }

    // Shut to everybody but the one name the sample lets read the record, and shut as a 404: what a
    // host that never mapped the explorer would answer.
    [Test]
    public async Task TheRecordIsShutToEverybodyButItsReviewer()
    {
        var page = await SignedIn("joe.curious");
        await Ask(page, Names);

        var statuses = await page.EvaluateAsync<int[]>(
            """
            async () => [
                (await fetch('/scry-disclosures/')).status,
                (await fetch('/scry-disclosures/css/app.css')).status,
                (await fetch('/scry-disclosures/api/catalog')).status,
                (await fetch('/scry-disclosures/api/callers', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"caller":"joe.curious"}' })).status
            ]
            """);

        await Assert.That(statuses).IsEquivalentTo([404, 404, 404, 404]);
    }

    // The theme the page is given is kept, and is the one the query explorer on the same host reads.
    [Test]
    public async Task TheThemeIsKept()
    {
        var page = await SignedIn(Reviewer);
        await Explore(page, "status");
        await Answered(page, "status-view");

        var toggle = page.Locator("[data-testid='theme-toggle']");
        await toggle.ClickAsync();
        await toggle.ClickAsync();

        await Assert.That(await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme")).IsEqualTo("dark");
        await Assert.That(await page.EvaluateAsync<string>("() => localStorage.getItem('scry-theme')")).IsEqualTo("dark");
        await page.ReloadAsync();
        await Answered(page, "status-view");
        await Assert.That(await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme")).IsEqualTo("dark");
    }
}
