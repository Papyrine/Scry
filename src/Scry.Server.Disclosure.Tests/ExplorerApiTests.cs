using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

/// <summary>
/// The disclosure explorer's questions, asked over HTTP: what each is answered with, that every one
/// of them is in the record before it is answered, and that every way in is shut until a host opens it.
/// </summary>
public class ExplorerApiTests
{
    // The three questions the record exists for, an answer opened, and how the record stands — asked
    // in the order somebody chasing one disclosure would ask them.
    [Test]
    public async Task TheQuestionsTheRecordExistsFor()
    {
        await using var host = await ExplorerHost.Run();
        var morning = await host.Recorded();

        var catalog = await host.Answer("api/catalog");
        var rows = await host.Answer("api/rows", """{"source":"Patient","key":"1"}""");
        var callers = await host.Answer("api/callers", """{"caller":"dr.osei"}""");
        var members = await host.Answer("api/members", """{"caller":"nurse.kim","source":"Patient","member":"Diagnosis"}""");
        var never = await host.Answer("api/members", """{"caller":"dr.osei","source":"Patient","member":"Diagnosis"}""");
        var opened = await host.Answer($"api/events/{morning.Chart:D}");
        var status = await host.Answer("api/status");

        await VerifyJson(
            $$"""
              {
                "catalog": {{catalog}},
                "whoReceivedPatient1": {{rows}},
                "whatDrOseiReceived": {{callers}},
                "whetherNurseKimReceivedADiagnosis": {{members}},
                "whetherDrOseiDid": {{never}},
                "theChartOpened": {{opened}},
                "howTheRecordStands": {{status}}
              }
              """);
    }

    // Reading the record is itself recorded: each question is in the record, under whoever asked it
    // and with what it was asked about, by the time its answer has been written.
    [Test]
    public async Task EveryQuestionIsInTheRecord()
    {
        await using var host = await ExplorerHost.Run();
        var morning = await host.Recorded();

        await host.Answer("api/rows", """{"source":"Patient","key":"1"}""");
        await host.Answer("api/callers", """{"caller":"dr.osei"}""", reviewer: "auditor.lee");
        await host.Answer($"api/events/{morning.Names:D}");
        await host.Answer("api/catalog");

        var reviews = await host.Store.Reviews().ToListAsync();
        reviews.Reverse();
        var asked = (await host.Store.Content(reviews[0].Parameters!.Value))!.Value;
        using (Assert.Multiple())
        {
            // The catalog says what there is to ask about and shows nobody's data, so it is not one.
            await Assert.That(reviews.Select(_ => $"{_.Reviewer} {_.Question} {_.Results} {_.Events.Count}")).IsEquivalentTo(
                [
                    "records.officer Row 3 0",
                    "auditor.lee Caller 2 0",
                    "records.officer Event 1 1"
                ],
                CollectionOrdering.Matching);
            await Assert.That(reviews[2].Events).IsEquivalentTo([morning.Names]);
            await Assert.That(reviews.Select(_ => _.Node!).Distinct()).IsEquivalentTo(["ward-1"]);
            await Assert.That(asked.Kind).IsEqualTo(ScryDisclosureContentKind.Parameters);
            await Assert.That(Encoding.UTF8.GetString(asked.Bytes.Span)).IsEqualTo("""{"source":"Patient","key":"1","take":50}""");
        }
    }

    // The order is the point. With the record held shut, the answer does not arrive; it arrives once
    // the record has taken the question, and not before.
    [Test]
    public async Task AnAnswerWaitsForItsQuestionToBeRecorded()
    {
        var gate = new Gated();
        await using var host = await ExplorerHost.Run(sink: gate.Over);
        await host.Recorded();
        gate.Shut();

        var answer = host.Answer("api/rows", """{"source":"Patient","key":"1"}""");
        await gate.Reached;
        await Task.Delay(200);
        var early = answer.IsCompleted;
        gate.Open();
        var text = await answer;

        using (Assert.Multiple())
        {
            await Assert.That(early).IsFalse();
            await Assert.That(text).Contains("dr.osei");
        }
    }

    // A record that will not take the question means an answer that is not given, for every question
    // that shows somebody's data — and an erasure that is not made.
    [Test]
    public async Task AQuestionTheRecordWillNotTakeIsNotAnswered()
    {
        var gate = new Gated();
        await using var host = await ExplorerHost.Run(_ => _.EnableErase = _ => true, sink: gate.Over);
        var morning = await host.Recorded();
        gate.Refuse();

        (string Path, string? Body)[] questions =
        [
            ("api/rows", """{"source":"Patient","key":"1"}"""),
            ("api/callers", """{"caller":"dr.osei"}"""),
            ("api/members", """{"caller":"nurse.kim","source":"Patient","member":"Diagnosis"}"""),
            ($"api/events/{morning.Chart:D}", null),
            ("api/status", null),
            ("api/export", $$"""{"format":"csv","event":"{{morning.Chart:D}}"}"""),
            ("api/erase", """{"source":"Patient","key":"1","confirm":"1"}""")
        ];
        var answers = new List<string>();
        foreach (var (path, body) in questions)
        {
            using var response = await host.Ask(path, body);
            answers.Add($"{path.Split('/')[1]} {(int) response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        var chart = (await host.Store.Reconstruct(morning.Chart))!;
        using (Assert.Multiple())
        {
            await Assert.That(answers.Select(_ => _.Split(' ')[1]).Distinct()).IsEquivalentTo(["500"]);
            await Assert.That(answers.Where(_ => _.Contains("Fracture") || _.Contains("dr.osei"))).IsEmpty();
            await Assert.That(answers[0]).IsEqualTo("""rows 500 {"message":"That this was asked could not be recorded, so nothing was shown."}""");
            await Assert.That(chart.Units[0].Erased).IsFalse();
            await Assert.That(await host.Store.Erasures().ToListAsync()).IsEmpty();
        }
    }

    // Whoever opens an answer has seen what it carried, so from then on they are among those who
    // received its rows — and a list, which shows who and when and nothing of what, does not do that.
    [Test]
    public async Task OpeningAnAnswerMakesTheReaderAReceiverOfItsRows()
    {
        await using var host = await ExplorerHost.Run();
        var morning = await host.Recorded();

        await host.Answer("api/rows", """{"source":"Ward","key":"1"}""");
        var listed = await host.Store.ReceiversOf("Ward", [1]).ToListAsync();
        await host.Answer($"api/events/{morning.Chart:D}");
        var opened = await host.Store.ReceiversOf("Ward", [1]).ToListAsync();
        var asked = JsonDocument.Parse(await host.Answer("api/rows", """{"source":"Ward","key":"1"}"""));

        using (Assert.Multiple())
        {
            await Assert.That(listed.Select(_ => _.Review?.Reviewer ?? _.Event.Caller!)).IsEquivalentTo(["nurse.kim"]);
            await Assert.That(opened.Select(_ => _.Review?.Reviewer ?? _.Event.Caller!)).IsEquivalentTo(["records.officer", "nurse.kim"], CollectionOrdering.Matching);
            await Assert.That(asked.RootElement.GetProperty("rows")[0].GetProperty("review").GetProperty("reviewer").GetString()).IsEqualTo("records.officer");
            await Assert.That(asked.RootElement.GetProperty("rows")[0].GetProperty("via").GetString()).IsEqualTo("Ward");
        }
    }

    // Nobody to record a question under means nothing shown — unless the host has said the record
    // may be kept against nobody, in which case it is, and says so.
    [Test]
    public async Task AReaderNobodyCanNameIsShownNothing()
    {
        await using var host = await ExplorerHost.Run();
        await using var open = await ExplorerHost.Run(audit: _ => _.AllowAnonymous = true);
        await host.Recorded();
        await open.Recorded();

        using var refused = await host.Ask("api/rows", """{"source":"Patient","key":"1"}""", reviewer: null);
        using var catalog = await host.Ask("api/catalog", reviewer: null);
        using var allowed = await open.Ask("api/rows", """{"source":"Patient","key":"1"}""", reviewer: null);

        using (Assert.Multiple())
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Nobody is named as reading the record");
            await Assert.That(await host.Store.Reviews().ToListAsync()).IsEmpty();

            // The page still loads, and is told that it is being read by nobody.
            await Assert.That(await catalog.Content.ReadAsStringAsync()).DoesNotContain("\"reviewer\"");
            await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await open.Store.Reviews().ToListAsync()).Single().Reviewer).IsNull();
        }
    }

    // Shut until opened, each on its own: the explorer in anything but development, exporting with
    // it, and erasing everywhere. Shut is a 404, which is what a host that never mapped it answers.
    [Test]
    public async Task EveryWayInIsShutUntilAHostOpensIt()
    {
        await using var production = await ExplorerHost.Run(defaults: true);
        await using var development = await ExplorerHost.Run(defaults: true, environment: "Development");
        await using var opened = await ExplorerHost.Run(_ => _.EnableExport = _ => false);
        var morning = await development.Recorded();
        var export = $$"""{"format":"csv","event":"{{morning.Chart:D}}"}""";
        var erase = """{"source":"Patient","key":"1","confirm":"1"}""";

        var seen = new List<string>();
        foreach (var (name, host) in new[] {("production", production), ("development", development), ("no-export", opened)})
        {
            foreach (var (path, body) in new (string, string?)[] {("", null), ("css/app.css", null), ("api/catalog", null), ("api/status", null), ("api/export", export), ("api/erase", erase)})
            {
                using var response = await host.Ask(path, body);
                seen.Add($"{name} /{path} {(int) response.StatusCode}");
            }
        }

        await Assert.That(seen).IsEquivalentTo(
            [
                "production / 404",
                "production /css/app.css 404",
                "production /api/catalog 404",
                "production /api/status 404",
                "production /api/export 404",
                "production /api/erase 404",
                "development / 200",
                "development /css/app.css 200",
                "development /api/catalog 200",
                "development /api/status 200",
                "development /api/export 200",
                "development /api/erase 404",
                "no-export / 200",
                "no-export /css/app.css 200",
                "no-export /api/catalog 200",
                "no-export /api/status 200",
                "no-export /api/export 404",
                "no-export /api/erase 404"
            ],
            CollectionOrdering.Matching);
    }

    // A question comes from the explorer's own page or from no page at all. A browser says where a
    // request came from, and one made by another site's page would otherwise be recorded in the
    // reader's name as a question the reader never asked.
    [Test]
    [Arguments("cross-site", 403, 0)]
    [Arguments("same-site", 403, 0)]
    [Arguments("same-origin", 200, 1)]
    [Arguments("none", 200, 1)]
    public async Task AQuestionIsOnlyAskedFromTheExplorersOwnPage(string site, int expected, int recorded)
    {
        await using var host = await ExplorerHost.Run();
        var morning = await host.Recorded();

        using var response = await host.Ask($"api/events/{morning.Chart:D}", site: site);

        using (Assert.Multiple())
        {
            await Assert.That((int) response.StatusCode).IsEqualTo(expected);
            await Assert.That(await host.Store.Reviews().ToListAsync()).Count().IsEqualTo(recorded);
        }
    }

    // A form cannot send application/json, so asking for it keeps a cross-site form from being read
    // as a question — the rule the query endpoints apply. And a question is a small thing.
    [Test]
    public async Task AQuestionIsJsonAndSmall()
    {
        await using var host = await ExplorerHost.Run();
        await host.Recorded();
        var endless = $$"""{"source":"Patient","key":"{{new string('1', 70_000)}}"}""";

        using var form = await host.Ask("api/rows", "source=Patient&key=1", type: "application/x-www-form-urlencoded");
        using var text = await host.Ask("api/rows", """{"source":"Patient","key":"1"}""", type: "text/plain");
        using var broken = await host.Ask("api/rows", """{"source":""");
        using var empty = await host.Ask("api/rows", "null");
        using var keyless = await host.Ask("api/rows", """{"source":"Patient","key":""}""");
        using var sourceless = await host.Ask("api/rows", """{"key":"1"}""");
        using var huge = await host.Ask("api/rows", endless);
        using var elsewhere = await host.Ask("api/nothing-here");

        using (Assert.Multiple())
        {
            await Assert.That(form.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
            await Assert.That(text.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
            await Assert.That(broken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(empty.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(keyless.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await keyless.Content.ReadAsStringAsync()).Contains("A key is its values in the key");
            await Assert.That(sourceless.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(huge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
            await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await host.Store.Reviews().ToListAsync()).IsEmpty();
        }
    }

    // What the explorer is told is somebody's data, so nothing keeps a copy of it; the page and its
    // assets are the program, so they are revalidated against a tag of their content. And the page
    // runs under a policy that gives what it is shown nowhere else to go.
    [Test]
    public async Task AnswersAreNeverStoredAndThePageIsHeldToItsPolicy()
    {
        await using var host = await ExplorerHost.Run();
        await host.Recorded();

        using var bare = await host.Client.GetAsync("/scry-disclosures");
        using var page = await host.Client.GetAsync("/scry-disclosures/");
        using var style = await host.Client.GetAsync("/scry-disclosures/css/app.css");
        using var answer = await host.Ask("api/catalog");
        using var again = new HttpRequestMessage(HttpMethod.Get, "/scry-disclosures/");
        again.Headers.IfNoneMatch.Add(page.Headers.ETag!);
        using var unchanged = await host.Client.SendAsync(again);
        using var styleAgain = new HttpRequestMessage(HttpMethod.Get, "/scry-disclosures/css/app.css");
        styleAgain.Headers.IfNoneMatch.Add(style.Headers.ETag!);
        using var styleUnchanged = await host.Client.SendAsync(styleAgain);
        var html = await page.Content.ReadAsStringAsync();
        var policy = page.Headers.GetValues("Content-Security-Policy").Single();

        using (Assert.Multiple())
        {
            await Assert.That(html).Contains("<base href=\"/scry-disclosures/\" />");
            await Assert.That(policy).StartsWith("default-src 'self'; script-src 'self' 'wasm-unsafe-eval' 'sha256-");
            await Assert.That(policy).EndsWith("; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'none'");
            await Assert.That(policy).DoesNotContain("unsafe-inline");
            await Assert.That(page.Headers.CacheControl!.ToString()).IsEqualTo("no-cache");
            await Assert.That(unchanged.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That(unchanged.Headers.GetValues("Content-Security-Policy").Single()).IsEqualTo(policy);
            await Assert.That(style.Content.Headers.ContentType!.MediaType).IsEqualTo("text/css");
            await Assert.That(styleUnchanged.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That(answer.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
            await Assert.That(answer.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");

            // Without its trailing slash the page is sent to the address with one, where it is inside
            // its own base.
            await Assert.That(bare.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(bare.Headers.Location!.ToString()).IsEqualTo("/scry-disclosures/");
        }
    }

    // The policy names the hash of each inline script the page holds, taken after the route has been
    // written into the page — so a token that ever lands inside a script takes its hash with it.
    [Test]
    public async Task ThePolicyIsOfThePageAsItIsServed()
    {
        var page = ScryDisclosureExplorerExtensions.Build(
            """
            <base href="__SCRY_BASE__" />
            <script>var home = '__SCRY_BASE__';</script>
            <script src="js/disclosure.js"></script>
            """,
            "/audit");
        var served = "var home = '/audit/';";
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(served)));

        using (Assert.Multiple())
        {
            await Assert.That(page.Html).Contains(served);
            await Assert.That(page.Policy).Contains($"script-src 'self' 'wasm-unsafe-eval' 'sha256-{hash}';");
            await Assert.That(page.Tag).IsEqualTo($"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(page.Html)))}\"");
        }
    }

    // Erasing is off until a host turns it on, wants the key typed twice, and is recorded twice over:
    // as a question, before it is done, and as the erasure, naming who asked.
    [Test]
    public async Task ErasingNeedsItsOwnGuardAndTheKeyAgain()
    {
        await using var shut = await ExplorerHost.Run();
        await using var host = await ExplorerHost.Run(_ => _.EnableErase = _ => true);
        await shut.Recorded();
        var morning = await host.Recorded();

        using var refused = await shut.Ask("api/erase", """{"source":"Patient","key":"1","confirm":"1"}""");
        using var mistyped = await host.Ask("api/erase", """{"source":"Patient","key":"1","confirm":"2"}""");
        var untouched = (await host.Store.Reconstruct(morning.Names))!.Units[0].Erased;
        var done = await host.Answer("api/erase", """{"source":"Patient","key":"1","confirm":"[1]"}""");

        var names = (await host.Store.Reconstruct(morning.Names))!;
        var erasure = (await host.Store.Erasures().ToListAsync()).Single();
        var review = (await host.Store.Reviews().ToListAsync()).Single();
        var catalog = await host.Answer("api/catalog");
        using (Assert.Multiple())
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(mistyped.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await mistyped.Content.ReadAsStringAsync()).Contains("The key typed again is not the key. Nothing was erased.");
            await Assert.That(untouched).IsFalse();
            await Assert.That(done).Contains("\"units\":2");
            await Assert.That(names.Units[0].Erased).IsTrue();
            await Assert.That(names.Units[1].Erased).IsFalse();
            await Assert.That(erasure.By).IsEqualTo("records.officer");
            await Assert.That(erasure.Key).IsEqualTo("[1]");
            await Assert.That(review.Question).IsEqualTo(ScryDisclosureQuestion.Erase);
            await Assert.That(review.Reviewer).IsEqualTo("records.officer");
            await Assert.That(catalog).Contains("\"erase\":true");
            await Assert.That(await shut.Answer("api/catalog")).Contains("\"erase\":false");
        }
    }

    // An export is every answer of a question leaving at once, with what each carried. Each of those
    // answers is named in the record before the file is written, and a cell a spreadsheet would run
    // as a formula is made text.
    [Test]
    public async Task AnExportNamesEveryAnswerItWrites()
    {
        await using var host = await ExplorerHost.Run();
        var morning = await host.Recorded();
        var plan = Clinic.Content(ScryDisclosureContentKind.Sql, "-- the patients of one ward\nSELECT [p].[Name] FROM [Patients] AS [p]");
        var sql = ExplorerHost.Begin(4, "=cmd|' /C calc'!A0", kind: ScryDisclosureKind.SqlPreview);
        await host.Store.AppendAsync(Clinic.Part(sql.Id, 0, sql, close: ExplorerHost.Close(ScryDisclosureOutcome.Released, 1), rows: [(0, plan, null, null)]), Cancel.None);

        using var csv = await host.Ask("api/export", """{"format":"csv","caller":{"caller":"dr.osei"}}""");
        using var row = await host.Ask("api/export", """{"format":"csv","row":{"source":"Patient","key":"2"}}""");
        using var formula = await host.Ask("api/export", """{"format":"csv","caller":{"caller":"=cmd|' /C calc'!A0"}}""");
        using var json = await host.Ask("api/export", $$"""{"format":"json","event":"{{morning.Chart:D}}"}""");
        using var neither = await host.Ask("api/export", """{"format":"csv"}""");
        using var both = await host.Ask("api/export", $$"""{"format":"csv","caller":{"caller":"dr.osei"},"event":"{{morning.Chart:D}}"}""");
        using var unknown = await host.Ask("api/export", """{"format":"xlsx","caller":{"caller":"dr.osei"}}""");

        var reviews = await host.Store.Reviews().ToListAsync();
        reviews.Reverse();
        using (Assert.Multiple())
        {
            await Assert.That(csv.Content.Headers.ContentDisposition!.FileName).IsEqualTo("disclosures-20260301-130002.csv");
            await Assert.That(csv.Content.Headers.ContentType!.ToString()).IsEqualTo("text/csv; charset=utf-8");

            // Led by a byte-order mark, which is what tells a spreadsheet the file is UTF-8.
            await Assert.That((await csv.Content.ReadAsByteArrayAsync())[..3]).IsEquivalentTo(new byte[] {0xEF, 0xBB, 0xBF}, CollectionOrdering.Matching);
            await Assert.That(neither.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(both.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

            // One review for each export that was written, each naming what it wrote.
            await Assert.That(reviews.Select(_ => _.Question).Distinct()).IsEquivalentTo([ScryDisclosureQuestion.Export]);
            await Assert.That(reviews.Select(_ => _.Events.Count)).IsEquivalentTo([2, 1, 1, 1], CollectionOrdering.Matching);
            await Assert.That(reviews[0].Events).IsEquivalentTo([morning.Cut, morning.Names], CollectionOrdering.Matching);
            await Assert.That(reviews[3].Events).IsEquivalentTo([morning.Chart]);
        }

        await Verify(
                $"""
                 --- what dr.osei received, as csv
                 {await csv.Content.ReadAsStringAsync()}
                 --- who received Patient[2], as csv
                 {await row.Content.ReadAsStringAsync()}
                 --- a caller named as a formula, as csv
                 {await formula.Content.ReadAsStringAsync()}
                 --- one answer, as json
                 {await json.Content.ReadAsStringAsync()}
                 """)
            .ScrubInlineGuids();
    }

    // An export holds so many answers and no more. Past that it holds the newest, and says so: in a
    // header the page reads, and in the file whoever opens it reads.
    [Test]
    public async Task AnExportThatIsCutShortSaysSo()
    {
        await using var host = await ExplorerHost.Run(_ => _.ExportLimit = 2);
        var row = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Ada\"}");
        foreach (var minute in Enumerable.Range(1, 3))
        {
            var begin = ExplorerHost.Begin(minute, "dr.osei");
            await host.Store.AppendAsync(Clinic.Part(begin.Id, 0, begin, close: ExplorerHost.Close(ScryDisclosureOutcome.Released, 1), rows: [(0, row, "Patient", "[1]")]), Cancel.None);
        }

        using var csv = await host.Ask("api/export", """{"format":"csv","caller":{"caller":"dr.osei"}}""");
        using var json = await host.Ask("api/export", """{"format":"json","row":{"source":"Patient","key":"1"}}""");
        using var whole = await host.Ask("api/export", """{"format":"json","caller":{"caller":"dr.osei","from":"2026-03-01T09:02:30Z"}}""");
        var lines = (await csv.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        using (Assert.Multiple())
        {
            await Assert.That(csv.Headers.Contains("Scry-Export-Cut")).IsTrue();
            await Assert.That(lines).Count().IsEqualTo(4);
            await Assert.That(lines[1]).StartsWith("2026-03-01T09:03:00.0000000+00:00,dr.osei,List,Patient,");
            await Assert.That(lines[2]).StartsWith("2026-03-01T09:02:00.0000000+00:00,");
            await Assert.That(lines[3]).IsEqualTo("(cut short: only the newest 2 events are here)");
            await Assert.That(await json.Content.ReadAsStringAsync()).Contains("\"cut\": true");
            await Assert.That(whole.Headers.Contains("Scry-Export-Cut")).IsFalse();
            await Assert.That(await whole.Content.ReadAsStringAsync()).Contains("\"cut\": false");
        }
    }

    // A store with nothing of its own to check is not offered a check, and one that has is: the
    // explorer goes by what the host's services say the store can do.
    [Test]
    public async Task ACheckIsOfferedWhereTheStoreHasSomethingToCheck()
    {
        var checker = new Checker();
        await using var plain = await ExplorerHost.Run();
        await using var chained = await ExplorerHost.Run(services: _ => _.AddSingleton<IScryDisclosureVerifier>(checker));

        using var none = await plain.Ask("api/verify", "{}");
        var check = await chained.Answer("api/verify", """{"from":7,"count":99999}""");

        using (Assert.Multiple())
        {
            await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await plain.Answer("api/catalog")).Contains("\"verify\":false");
            await Assert.That(await chained.Answer("api/catalog")).Contains("\"verify\":true");
            await Assert.That(check).Contains("\"records\":3,\"intact\":false,\"brokenAt\":9");
            await Assert.That(checker.Asked.From).IsEqualTo(7);
            await Assert.That(checker.Asked.Count).IsEqualTo(10_000);
            await Assert.That((await chained.Store.Reviews().ToListAsync()).Single().Question).IsEqualTo(ScryDisclosureQuestion.Verify);
        }
    }

    // Said when the host starts, where whoever wired it is reading, rather than the first time
    // somebody opens the page.
    [Test]
    public async Task AHostWithNoRecordIsToldAtStartup()
    {
        var silent = Hosted(_ => { });
        var unread = Hosted(_ => _.UseDisclosureAudit(new Unreadable()));

        var none = Assert.ThrowsExactly<InvalidOperationException>(() => silent.MapScryDisclosureExplorer());
        var blind = Assert.ThrowsExactly<InvalidOperationException>(() => unread.MapScryDisclosureExplorer());
        var limitless = Assert.ThrowsExactly<ArgumentException>(() => unread.MapScryDisclosureExplorer(_ => _.ExportLimit = 0));

        using (Assert.Multiple())
        {
            await Assert.That(none.Message).Contains("this host keeps none");
            await Assert.That(blind.Message).Contains("no IScryDisclosureReader is registered");
            await Assert.That(limitless.Message).Contains("ExportLimit must be greater than zero");
        }

        await silent.DisposeAsync();
        await unread.DisposeAsync();
    }

    // A store is usually more than a sink. One handed over as the sink is found in the host's
    // services as whatever else it is, so that the explorer finds what reads the record without the
    // host registering each — and still is where a journal has been put in front of it.
    [Test]
    public async Task AStoreHandedOverAsTheSinkIsFoundAsWhatElseItIs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scry-journal-tests", Guid.NewGuid().ToString("N"));
        var store = new ScryMemoryDisclosureStore();
        var plain = new ServiceCollection();
        plain.AddScry<ClinicContext>(_ => _.UseDisclosureAudit(store));
        var journaled = new ServiceCollection();
        journaled.AddScry<ClinicContext>(_ => _.UseDisclosureAudit(store, _ => _.UseJournal(directory)));

        try
        {
            await using var direct = plain.BuildServiceProvider();
            await using var behind = journaled.BuildServiceProvider();
            using (Assert.Multiple())
            {
                await Assert.That(direct.GetRequiredService<IScryDisclosureSink>()).IsSameReferenceAs(store);
                await Assert.That(direct.GetRequiredService<IScryDisclosureReader>()).IsSameReferenceAs(store);
                await Assert.That(direct.GetRequiredService<IScryDisclosureEraser>()).IsSameReferenceAs(store);
                await Assert.That(direct.GetRequiredService<IScryDisclosureStatus>()).IsSameReferenceAs(store);
                await Assert.That(direct.GetService<IScryDisclosureVerifier>()).IsNull();
                await Assert.That(behind.GetRequiredService<IScryDisclosureSink>()).IsTypeOf<ScryDisclosureJournal>();
                await Assert.That(behind.GetRequiredService<IScryDisclosureReader>()).IsSameReferenceAs(store);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A key is read as it is typed: as the list of its values, with or without the brackets, or as
    // one piece of text where it is neither.
    [Test]
    [Arguments("1", "[1]")]
    [Arguments(" 1 ", "[1]")]
    [Arguments("[1]", "[1]")]
    [Arguments("\"A\", 7", "[\"A\",7]")]
    [Arguments("[\"A\",7]", "[\"A\",7]")]
    [Arguments("\"7\"", "[\"7\"]")]
    [Arguments("1.50", "[1.50]")]
    [Arguments("true, null", "[true,null]")]
    [Arguments("North", "[\"North\"]")]
    [Arguments("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "[\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"]")]
    [Arguments("2026-03-01", "[\"2026-03-01\"]")]
    [Arguments("007", "[\"007\"]")]
    public async Task AKeyIsReadAsItIsTyped(string typed, string read)
    {
        await Assert.That(DisclosureKeys.TryRead(typed, out var key)).IsTrue();
        await Assert.That(ScryDisclosureEntity.KeyOf(key!)).IsEqualTo(read);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task AKeyIsSomething(string? typed) =>
        await Assert.That(DisclosureKeys.TryRead(typed, out _)).IsFalse();

    // What a view was asked is the fragment of the page's address, in one form for one question, and
    // read back as what was written. Anything that is no view of the page reads as where it starts.
    [Test]
    public async Task AQuestionIsAnAddress()
    {
        var row = DisclosureLink.To(DisclosureLink.Row, ("source", "Patient"), ("key", "\"A&B\", 7"), ("from", null), ("to", ""));
        var read = DisclosureLink.Parse($"#{row}");

        using (Assert.Multiple())
        {
            await Assert.That(row.ToString()).IsEqualTo("row?key=%22A%26B%22%2C%207&source=Patient");
            await Assert.That(read.View).IsEqualTo("row");
            await Assert.That(read["key"]).IsEqualTo("\"A&B\", 7");
            await Assert.That(read["source"]).IsEqualTo("Patient");
            await Assert.That(read["from"]).IsNull();
            await Assert.That(DisclosureLink.Parse("status").ToString()).IsEqualTo("status");
            await Assert.That(DisclosureLink.Parse("#event?id=1&id=2")["id"]).IsEqualTo("2");
            await Assert.That(DisclosureLink.Parse("#caller?caller=%ZZ")["caller"]).IsEqualTo("%ZZ");
            await Assert.That(DisclosureLink.Parse(null)).IsEqualTo(DisclosureLink.Home);
            await Assert.That(DisclosureLink.Parse("#elsewhere?x=1")).IsEqualTo(DisclosureLink.Home);
            await Assert.That(DisclosureLink.Parse("")).IsEqualTo(DisclosureLink.Home);
        }
    }

    static WebApplication Hosted(Action<ScryOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScry<ClinicContext>(configure);
        return builder.Build();
    }

    // A sink that can be held shut, or made to refuse: what shows that an answer waits on its record.
    sealed class Gated
    {
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        volatile bool shut;
        volatile bool refusing;

        public Task Reached => reached.Task;

        public IScryDisclosureSink Over(ScryMemoryDisclosureStore store) =>
            new Sink(this, store);

        public void Shut() =>
            shut = true;

        public void Open() =>
            open.TrySetResult();

        public void Refuse() =>
            refusing = true;

        sealed class Sink(Gated gate, ScryMemoryDisclosureStore store) :
            IScryDisclosureSink
        {
            public void Append(ScryDisclosureBatch batch) =>
                AppendAsync(batch, Cancel.None).AsTask().GetAwaiter().GetResult();

            public async ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
            {
                if (gate.refusing)
                {
                    throw new IOException("The record is not there.");
                }

                if (gate.shut)
                {
                    gate.reached.TrySetResult();
                    await gate.open.Task.WaitAsync(cancel);
                }

                await store.AppendAsync(batch, cancel);
            }
        }
    }

    // Something that says it checked, and what it was asked to.
    sealed class Checker :
        IScryDisclosureVerifier
    {
        public (long? From, int Count) Asked { get; private set; }

        public ValueTask<ScryDisclosureChainCheck> Verify(long? from = null, int count = 10_000, Cancel cancel = default)
        {
            Asked = (from, count);
            return new(
                new ScryDisclosureChainCheck(ExplorerHost.Start, 3, Intact: false)
                {
                    BrokenAt = 9
                });
        }
    }

    // A sink that keeps what it is handed and can say nothing of it afterwards.
    sealed class Unreadable :
        IScryDisclosureSink
    {
        public void Append(ScryDisclosureBatch batch)
        {
        }

        public ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel) =>
            ValueTask.CompletedTask;
    }
}
