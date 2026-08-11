using System.Text.Json;
using System.Xml.Linq;

// Drives the live WebAssembly UI in a headless browser, asserting behaviour and snapshotting the
// rendered markup as text. The pixel snapshots live in UiScreenshotTests.
// Categorised "Browser" so a run can opt out: the browser download and WASM boot are heavier than
// the in-process tests.
[TestFixture]
[Category("Browser")]
public class UiSnapshotTests :
    BrowserFixture
{
    [Test]
    public async Task HomePage()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync(BaseUrl);

        // Wait until the WebAssembly app has run its queries and rendered the result tables.
        await page.WaitForSelectorAsync("table tbody tr");
        await Assertions.Expect(page.Locator("table")).ToHaveCountAsync(4);

        // Just the app's own markup, and as text: this is the snapshot that stays readable in a diff
        // and portable across machines. UiScreenshotTests.SampleHomePage captures the same page as a
        // rendering, whole document and pixels included.
        var rendered = await page.Locator("#app").InnerHTMLAsync();
        await Verify(rendered);
    }

    // The explorer fetches GET {route}/introspect on load to learn the queryable schema. Exercises the
    // endpoint end-to-end (routing, Development guard, ScryJson serialization) against the live server.
    [Test]
    public async Task ExplorerIntrospectionEndpoint()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"{BaseUrl}/scry/introspect");

        await Verify(json);
    }

    // Verifies the Scry explorer (a separate Blazor WASM app, embedded in and served by the
    // Scry.Server.Explorer package under /scry) boots from the embedded assets in a real browser.
    [Test]
    public async Task ExplorerBoots()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");

        // Allow the WASM runtime to download and boot before asserting the app rendered.
        await page.WaitForSelectorAsync("[data-testid='explorer-title']", 30);
        var title = page.Locator("[data-testid='explorer-title']");
        await Assertions.Expect(title).ToHaveTextAsync("Scry Explorer");

        // The Monaco editor mounts only if the embedded _content/BlazorMonaco assets are served.
        await page.WaitForSelectorAsync(".monaco-editor", 30);

        // The action buttons carry explanatory tooltips.
        var completeTooltip = await page.Locator("[data-testid='complete']").GetAttributeAsync("title");
        Assert.That(completeTooltip, Does.Contain("IntelliSense"));
    }

    // Snapshots the explorer's own rendered markup (the App.razor chrome: heading, action bar, and
    // the conditional result panes) as a regression guard on the page structure. Monaco's editor DOM
    // and the Roslyn completion list are non-deterministic (dynamic ids, engine-ordered items), so
    // both are reduced to their host element before the snapshot — leaving exactly the markup the
    // component itself emits.
    [Test]
    public async Task ExplorerShellMarkup()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        // Wait for the schema to load so the fully-initialised shell (enabled buttons, no "Loading
        // schema…") is what gets captured, then strip the volatile inner content.
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        var markup = await page.EvaluateAsync<string>(
            """
            () => {
                const app = document.querySelector('#app').cloneNode(true);

                // Monaco fills the editor host with non-deterministic DOM (dynamic ids, measure
                // spans); reduce it to its bare host so the snapshot tracks App.razor, not Monaco.
                const editor = app.querySelector('.scry-editor');
                if (editor) {
                    editor.textContent = '';
                    for (const attribute of [...editor.attributes]) {
                        if (attribute.name !== 'id' && attribute.name !== 'class') {
                            editor.removeAttribute(attribute.name);
                        }
                    }
                    editor.setAttribute('class', 'scry-editor');
                }

                // The completion list is Roslyn output (engine-ordered); keep the container, drop the
                // items — the items themselves are asserted by ExplorerCompletion.
                const completions = app.querySelector("[data-testid='completions']");
                if (completions) {
                    completions.textContent = '';
                }

                return app.innerHTML;
            }
            """);

        await Verify(markup);
    }

    // Regression test for the editor-height bug. Monaco does not size itself: its host element needs
    // an explicit height, or it lays out into a ~0-height box that renders a clipped sliver and
    // silently swallows clicks/keystrokes — you cannot type into it. Asserts the host has a real
    // height and that clicking into the editor and typing actually updates the model.
    [Test]
    public async Task ExplorerEditorAcceptsTyping()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);

        var height = await page.EvaluateAsync<double>(
            "() => document.querySelector('.scry-editor').getBoundingClientRect().height");
        Assert.That(height, Is.GreaterThan(100), "editor host height (the bug rendered it ~0)");

        // Type the way a user does: click into the editor to focus it, then type on the keyboard.
        await page.SetEditorValueAsync("");
        await page.Locator(".monaco-editor").ClickAsync();
        await page.Keyboard.TypeAsync("Query");

        var value = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
        Assert.That(value, Is.EqualTo("Query"), "typed text should reach the editor model");
    }

    // Proves Roslyn runs in the browser and completes against the introspected schema: the explorer
    // auto-runs completion for "Query.Employee.Where(_ => _." on load and should offer Employee members.
    [Test]
    public async Task ExplorerCompletion()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");

        // Roslyn init + first completion in the WASM interpreter is slow on a cold load.
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);
        var items = await page.Locator("[data-testid='completions'] li").AllInnerTextsAsync();

        Assert.That(items, Does.Contain("Active"));
        Assert.That(items, Does.Contain("Name"));
        Assert.That(items, Does.Contain("Status"));
        Assert.That(items, Does.Contain("Manager"));
    }

    // Regression guard for the class of failure where a Microsoft.CodeAnalysis (Roslyn) or runtime upgrade
    // breaks the in-browser completion engine at RUNTIME on the mono-wasm interpreter. The in-process Roslyn
    // tests (Scry.Explorer.Tests) run on desktop CoreCLR and stay green even when WASM is broken, so only a
    // real browser can catch this. Roslyn 5.6.0 regressed here: its completion path tripped a fatal
    // StackOverflowException (infinite System.Threading.Volatile.ReadBarrier recursion) that killed the WASM
    // runtime, so completions never rendered — and every other Explorer test failed only as an opaque 90s
    // "waiting for completions" timeout. This test watches the browser console for a fatal unhandled exception
    // and fails FAST with a message that names the cause. See the pin note in src/Directory.Packages.props.
    [Test]
    public async Task ExplorerCompletionDoesNotCrashWasmRuntime()
    {
        var page = await Browser.NewPageAsync();

        // Blazor logs unhandled .NET exceptions to console.error; the mono runtime prints
        // "FATAL UNHANDLED EXCEPTION" for a stack overflow; a boot-time asset failure aborts with
        // "Failed to start platform". Any of these means the runtime died — not that completion is slow.
        // (The benign PlatformNotSupportedException from Roslyn's persistent-storage probe logs a plain
        // "Unhandled Exception:" without these markers, and is deliberately not treated as fatal.)
        var fatal = new List<string>();
        void Watch(string text)
        {
            foreach (var marker in (string[])["StackOverflowException", "FATAL UNHANDLED EXCEPTION", "Failed to start platform"])
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    fatal.Add(text);
                    return;
                }
            }
        }

        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                Watch(message.Text);
            }
        };
        page.PageError += (_, error) => Watch(error);

        await page.GotoAsync($"{BaseUrl}/scry");

        // Race the two outcomes so a crash is reported in seconds (with a descriptive message) instead of a
        // blind 90s wait: either the completion list renders (runtime healthy) or a fatal error is logged.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (fatal.Count > 0)
            {
                Assert.Fail(
                    $"The Scry Explorer WASM runtime crashed during boot/completion: {fatal[0]}\n" +
                    "A Microsoft.CodeAnalysis (Roslyn) or .NET runtime upgrade likely regressed in-browser " +
                    "completion on the mono-wasm interpreter. See the pin note in src/Directory.Packages.props.");
            }

            if (await page.Locator("[data-testid='completions'] li").CountAsync() > 0)
            {
                break;
            }

            await Task.Delay(500);
        }

        // The runtime is alive AND completion produced results against the introspected schema.
        var items = await page.Locator("[data-testid='completions'] li").AllInnerTextsAsync();
        Assert.That(fatal, Is.Empty, $"fatal WASM runtime error(s): {string.Join(" || ", fatal)}");
        Assert.That(items, Does.Contain("Name"), "in-browser Roslyn completion returned no schema members");
    }

    // Full terminal support: the Scry terminal operators are discoverable via IntelliSense — completing
    // against the queryable itself (not a lambda member) offers ToListAsync/FirstAsync/etc.
    [Test]
    public async Task ExplorerCompletesTerminals()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Employee.");
        await page.Locator("[data-testid='complete']").ClickAsync();

        await page.WaitForFunctionAsync(
            "() => Array.from(document.querySelectorAll(\"[data-testid='completions'] li\")).some(li => li.textContent === 'ToListAsync')",
            null, new() { Timeout = 30_000 });
        var items = await page.Locator("[data-testid='completions'] li").AllInnerTextsAsync();

        Assert.That(items, Does.Contain("ToListAsync"));
        Assert.That(items, Does.Contain("ToArrayAsync"));
        Assert.That(items, Does.Contain("ToDictionaryAsync"));
        Assert.That(items, Does.Contain("FirstAsync"));
        Assert.That(items, Does.Contain("CountAsync"));
    }

    // The completion pills are buttons, not labels: clicking one types its text into the query at the
    // caret — which is what makes the list a way into the schema rather than only a view of it.
    [Test]
    public async Task ExplorerInsertsACompletionAtTheCursor()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.)");

        // The caret goes between the '.' and the ')' — where a member would be typed — rather than at the
        // end of the text, which is what proves the insert follows the cursor.
        await page.EvaluateAsync(
            "() => monaco.editor.getEditors()[0].setPosition({ lineNumber: 1, column: 29 })");

        await page.Locator("[data-testid='completions'] button:text-is('Active')").ClickAsync();

        var value = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
        Assert.That(value, Is.EqualTo("Query.Employee.Where(_ => _.Active)"));
    }

    // The list is the one for the caret rather than for the end of the text, and it follows the caret as
    // it moves. Without that the two halves of the panel disagree: it would offer an Employee member while
    // the caret sat in front of "Query", and clicking it would type that member there.
    [Test]
    public async Task ExplorerCompletesAtTheCursorRatherThanTheEndOfTheQuery()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        // The page opens with the caret at the end of the sample query, which ends mid-expression on an
        // Employee — so the members are what is offered.
        var atEnd = await page.Locator("[data-testid='completions'] li").AllInnerTextsAsync();
        Assert.That(atEnd, Does.Contain("ManagerId"));

        // The start of the query is not a place an Employee member can be written, so the members go away.
        // ManagerId rather than Status: Status is also an enum type, and a type name is in scope here.
        await page.EvaluateAsync(
            "() => monaco.editor.getEditors()[0].setPosition({ lineNumber: 1, column: 1 })");
        await page.WaitForFunctionAsync(
            "() => !Array.from(document.querySelectorAll(\"[data-testid='completions'] li\")).some(li => li.textContent === 'ManagerId')",
            null,
            new()
            {
                Timeout = 30_000
            });

        var atStart = await page.Locator("[data-testid='completions'] li").AllInnerTextsAsync();
        Assert.That(atStart, Does.Not.Contain("ManagerId"));
    }

    // The same insert onto a partially typed name. The pill overwrites what was typed rather than being
    // appended to it — clicking Active after typing "_.Ac" reads "_.Active", not "_.AcActive".
    [Test]
    public async Task ExplorerReplacesATypedPrefixWithTheCompletion()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.Ac)");

        // Caret immediately after the "Ac", which is the prefix the completion has to swallow.
        await page.EvaluateAsync(
            "() => monaco.editor.getEditors()[0].setPosition({ lineNumber: 1, column: 31 })");

        await page.Locator("[data-testid='completions'] button:text-is('Active')").ClickAsync();

        var value = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
        Assert.That(value, Is.EqualTo("Query.Employee.Where(_ => _.Active)"));
    }

    // The SQL pane: the server builds the query and reads its SQL back without executing it. The
    // sample server runs in Development, which is what the preview's own guard defaults to.
    [Test]
    public async Task ExplorerShowsSql()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .Select(_ => new { _.Name })
            """);
        await page.Locator("[data-testid='sql-preview']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='sql']", 30);

        var sql = await page.Locator("[data-testid='sql']").InnerTextAsync();

        Assert.That(sql, Does.Contain("SELECT"));
        Assert.That(sql, Does.Contain("[Employees]"));
        // The client's Where reached the SQL, so what is shown is this query rather than the table.
        Assert.That(sql, Does.Contain("WHERE"));
    }

    // A shared link carries the query in the fragment, so it survives a full reload — and a fragment
    // is never sent to the server, which is why the query goes there rather than in the query string.
    [Test]
    public async Task ExplorerSharesAQueryByLink()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        const string query =
            """
            Query.Employee
                .Where(_ => _.Active)
                .Select(_ => new { _.Name })
            """;
        await page.SetEditorValueAsync(query);
        await page.Locator("[data-testid='share']").ClickAsync();

        var shared = await page.EvaluateAsync<string>("() => location.href");
        Assert.That(shared, Does.Contain("#q="));

        // A fresh load of the shared link, not a fragment change on the running app: a hash-only
        // navigation would leave the editor as it is and prove nothing.
        var opened = await Browser.NewPageAsync();
        await opened.GotoAsync(shared);
        await opened.WaitForSelectorAsync(".monaco-editor", 30);
        await opened.WaitForFunctionAsync(
            "() => monaco.editor.getEditors().length > 0 && monaco.editor.getEditors()[0].getValue().length > 0",
            null,
            new() {Timeout = 30_000});

        var restored = await opened.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
        Assert.That(restored, Is.EqualTo(query));
    }

    // A link whose fragment is not a query the explorer wrote is ignored rather than surfaced: a URL
    // is untrusted input, and the explorer opens on its sample query instead of on an error.
    [Test]
    public async Task ExplorerIgnoresAMalformedShareLink()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry/#q=!!!not-base64!!!");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForFunctionAsync(
            "() => monaco.editor.getEditors().length > 0 && monaco.editor.getEditors()[0].getValue().length > 0",
            null,
            new() {Timeout = 30_000});

        var value = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");

        Assert.That(value, Does.StartWith("Query.Employee.Where"));
        Assert.That(await page.Locator("[data-testid='error']").CountAsync(), Is.Zero);
    }

    // A flat result exports in all three formats. The download itself is the browser's, so the test
    // intercepts the interop call and asserts the payload the UI produced — all from one run, because
    // a cold WASM+Roslyn boot costs far more than the three clicks it is set up for.
    [Test]
    public async Task ExplorerExportsResults()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .Select(_ => new { _.Name, _.Status })
            """);
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 30);
        await InterceptDownloadsAsync(page);

        var csv = await ExportAsync(page, "csv");
        Assert.That(csv.Name, Is.EqualTo("scry-result.csv"));
        Assert.That(csv.Text, Does.StartWith("name,status"));
        Assert.That(csv.Text, Does.Contain("Alice,FullTime"));
        // Excel reads a BOM-less UTF-8 CSV as the local codepage; the other two formats do not want one.
        Assert.That(csv.Bom, Is.True);

        var json = await ExportAsync(page, "json");
        Assert.That(json.Name, Is.EqualTo("scry-result.json"));
        Assert.That(json.Bom, Is.False, "a leading U+FEFF is not valid JSON");
        using var document = JsonDocument.Parse(json.Text);
        var rows = document.RootElement.EnumerateArray().ToList();
        Assert.That(rows, Is.Not.Empty);
        Assert.That(
            rows.Select(_ => _.GetProperty("name").GetString()),
            Does.Contain("Alice"));

        var xml = await ExportAsync(page, "xml");
        Assert.That(xml.Name, Is.EqualTo("scry-result.xml"));
        Assert.That(xml.Bom, Is.False);
        var results = XDocument.Parse(xml.Text).Root!;
        Assert.That(results.Name.LocalName, Is.EqualTo("results"));
        Assert.That(
            results.Elements("row").Select(_ => _.Element("name")!.Value),
            Does.Contain("Alice"));
    }

    // CSV is a grid, so it is offered only for a result that is one. Projecting into a navigation
    // nests an object inside every row: the CSV button goes away, and JSON and XML — which can carry
    // the nesting — stay.
    [Test]
    public async Task ExplorerOffersCsvOnlyForAFlatResult()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .Select(_ => new { _.Name, Department = new { _.Department!.Name } })
            """);
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 30);

        Assert.That(await page.Locator("[data-testid='csv']").CountAsync(), Is.Zero, "nested rows are not a grid");
        Assert.That(await page.Locator("[data-testid='json']").CountAsync(), Is.EqualTo(1));
        Assert.That(await page.Locator("[data-testid='xml']").CountAsync(), Is.EqualTo(1));

        await InterceptDownloadsAsync(page);
        var xml = await ExportAsync(page, "xml");

        // The navigation is a child element rather than a flattened column.
        var department = XDocument.Parse(xml.Text).Root!.Elements("row").First().Element("department");
        Assert.That(department, Is.Not.Null);
        Assert.That(department!.Element("name")!.Value, Is.Not.Empty);
    }

    /// <summary>
    /// The claim check end to end in a real browser: the query carries no bytes, every row carries
    /// the key, and the fetch link exchanges one for the file. Engineering holds a handbook; Sales
    /// holds a null one, which is a different answer from a refusal and says so without a download.
    /// </summary>
    [Test]
    public async Task ExplorerFetchesAnAttachment()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Department.OrderBy(_ => _.Name)");
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 60);

        // The attachment is in no projection the client could write, so the column below is the
        // explorer's own offer — built from the key the row does carry.
        var wire = await page.Locator("[data-testid='wire']").InnerTextAsync();
        Assert.That(wire, Does.Not.Contain("Handbook"));

        await InterceptDownloadsAsync(page);
        var links = page.Locator("[data-testid='attachment']");
        await Assertions.Expect(links).ToHaveCountAsync(2);

        // Engineering orders first, and its bytes arrive as the file the browser would have saved.
        await links.Nth(0).ClickAsync();
        await page.WaitForFunctionAsync("() => window.__file !== null", null, new() {Timeout = 30_000});
        Assert.That(
            await page.EvaluateAsync<string>("() => window.__file.name"),
            Is.EqualTo("Department-Handbook-1.bin"));
        Assert.That(
            await page.EvaluateAsync<string>("() => atob(window.__file.base64)"),
            Is.EqualTo("Engineering handbook."));

        // Sales holds none. Reported beside the row rather than as a page error: it is an answer
        // about that row, not a failure of the query.
        await links.Nth(1).ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='attachment-note']")).ToContainTextAsync("empty");
    }

    // Replaces the real file-saving interop with a recorder, so an export can be asserted rather than
    // landing in the browser's downloads directory.
    static Task InterceptDownloadsAsync(IPage page) =>
        page.EvaluateAsync(
            """
            () => {
                window.__file = null;
                window.scry.download = (name, text, type, bom) => window.__file = { name, text, type, bom };
                window.scry.downloadBytes = (name, base64, type) => window.__file = { name, base64, type };
            }
            """);

    static async Task<(string Name, string Text, bool Bom)> ExportAsync(IPage page, string format)
    {
        await page.EvaluateAsync("() => window.__file = null");
        await page.Locator($"[data-testid='{format}']").ClickAsync();
        await page.WaitForFunctionAsync("() => window.__file !== null", null, new() {Timeout = 30_000});

        return (
            await page.EvaluateAsync<string>("() => window.__file.name"),
            await page.EvaluateAsync<string>("() => window.__file.text"),
            await page.EvaluateAsync<bool>("() => !!window.__file.bom"));
    }

    // Proves the inline Monaco IntelliSense dropdown is wired to the Roslyn provider.
    [Test]
    public async Task ExplorerInlineSuggestions()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        // Wait for the schema to load (provider registered) — the auto-run completion list appears.
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        // Place the caret at the end (after "e.") and trigger IntelliSense via Monaco's API.
        await page.EvaluateAsync(
            """
            () => {
                const editor = monaco.editor.getEditors()[0];
                editor.focus();
                const model = editor.getModel();
                editor.setPosition({ lineNumber: 1, column: model.getLineMaxColumn(1) });
                editor.trigger('test', 'editor.action.triggerSuggest', {});
            }
            """);

        await page.WaitForSelectorAsync(".suggest-widget .monaco-list-row", 30);
        var rows = await page.Locator(".suggest-widget .monaco-list-row").AllInnerTextsAsync();
        Assert.That(rows.Any(_ => _.Contains("Active")), Is.True, $"suggest rows: {string.Join(" | ", rows)}");
    }

    // Proves end-to-end execution: the browser compiles + runs the query, translates it to the wire
    // format, POSTs it to /api/query, and renders the server's result.
    [Test]
    public async Task ExplorerRun()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        // Set a complete query, then run it.
        await page.SetEditorValueAsync(
            """
            Query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new { _.Name, _.Status })
            """);
        await page.Locator("[data-testid='run']").ClickAsync();

        await page.WaitForSelectorAsync("[data-testid='result-table']", 60);
        var wire = await page.Locator("[data-testid='wire']").InnerTextAsync();
        var result = await page.Locator("[data-testid='result']").InnerTextAsync();

        Assert.That(wire, Does.Contain("\"root\": \"Employee\"").Or.Contain("\"root\":\"Employee\""));
        Assert.That(result, Does.Contain("Aaron"));
        Assert.That(result, Does.Contain("Carol"));

        // Stage 3: results render as a table too.
        var table = await page.Locator("[data-testid='result-table']").InnerTextAsync();
        Assert.That(table, Does.Contain("Aaron"));
        Assert.That(table, Does.Contain("FullTime"));

        // Stage 3: the executed query is recorded in history. The list renders the multi-line
        // query whitespace-collapsed, so match fragments rather than the contiguous text.
        await page.WaitForSelectorAsync("[data-testid='history'] li", 10);
        var historyText = await page.Locator("[data-testid='history']").InnerTextAsync();
        Assert.That(historyText, Does.Contain("Query.Employee"));
        Assert.That(historyText, Does.Contain(".Where(_ => _.Active)"));
    }

    // A [BinaryTransfer] member never travels inside the JSON payload: the server diverts it to a raw
    // multipart part and leaves {"$bin":n} behind, which is a response the explorer cannot parse as
    // JSON at all. It reassembles one instead, so a diverted member reads as the base64 the same
    // byte[] would have arrived as without the attribute — the point of the attribute being that the
    // queryable surface does not change.
    [Test]
    public async Task ExplorerRunCarryingBinary()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync(
            """
            Query.Department
                .OrderBy(_ => _.Name)
                .Select(_ => new { _.Name, _.Logo })
            """);
        await page.Locator("[data-testid='run']").ClickAsync();

        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 60);
        var table = await page.Locator("[data-testid='result-table']").InnerTextAsync();
        var result = await page.Locator("[data-testid='result']").InnerTextAsync();

        // Engineering's seeded PNG signature, base64 — the encoding an undiverted byte[] arrives in.
        Assert.That(table, Does.Contain("iVBORw0KGgo="));
        Assert.That(result, Does.Contain("iVBORw0KGgo="));
        // Sales has no logo: a null stays inline in the JSON and produces no part at all.
        Assert.That(result, Does.Contain("null"));
        // The response pane shows the reassembled envelope rather than the placeholder or the raw
        // multipart body it arrived as.
        Assert.That(result, Does.Not.Contain("$bin"));
        Assert.That(result, Does.Not.Contain("Content-Type"));
        // A base64 cell is a scalar, so the result is still a grid and CSV stays on offer.
        Assert.That(await page.Locator("[data-testid='csv']").CountAsync(), Is.EqualTo(1));
    }

    // Terminal support: a plain LINQ '.ToList()' (the habitual way to ask for all rows) is folded into
    // an enumerate-all list request rather than enumerating synchronously (which would deadlock WASM).
    [Test]
    public async Task ExplorerRunToList()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Employee.ToList()");
        await page.Locator("[data-testid='run']").ClickAsync();

        await page.WaitForSelectorAsync("[data-testid='result-table']", 60);
        var table = await page.Locator("[data-testid='result-table']").InnerTextAsync();
        // No Where → all four employees, including the inactive Bob.
        Assert.That(table, Does.Contain("Aaron"));
        Assert.That(table, Does.Contain("Bob"));
    }

    // Terminal support: a scalar terminal (CountAsync) is reflected as a 'count' op in the wire
    // request and rendered as a scalar result.
    [Test]
    public async Task ExplorerRunCount()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .CountAsync()
            """);
        await page.Locator("[data-testid='run']").ClickAsync();

        await page.WaitForSelectorAsync("[data-testid='result-scalar']", 60);
        var wire = await page.Locator("[data-testid='wire']").InnerTextAsync();
        var scalar = await page.Locator("[data-testid='result-scalar']").InnerTextAsync();

        Assert.That(wire, Does.Contain("\"count\""));
        // Three active employees (Alice, Aaron, Carol).
        Assert.That(scalar.Trim(), Is.EqualTo("3"));
    }

    // Terminal support: a single-element terminal (FirstAsync) is reflected as a 'first' op and
    // rendered as a one-row result.
    [Test]
    public async Task ExplorerRunFirst()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new { _.Name, _.Status })
                .FirstAsync()
            """);
        await page.Locator("[data-testid='run']").ClickAsync();

        await page.WaitForSelectorAsync("[data-testid='result-table']", 60);
        var wire = await page.Locator("[data-testid='wire']").InnerTextAsync();
        var table = await page.Locator("[data-testid='result-table']").InnerTextAsync();

        Assert.That(wire, Does.Contain("\"first\""));
        // First active employee alphabetically.
        Assert.That(table, Does.Contain("Aaron"));
    }

    // Dark mode: the toggle retints Monaco (vs-dark class) + the page (data-theme), and the choice
    // persists across a reload (localStorage + the pre-paint data-theme script + the editor option).
    [Test]
    public async Task ExplorerDarkMode()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);

        // System → Light → Dark (deterministic regardless of the OS preference).
        var toggle = page.Locator("[data-testid='theme-toggle']");
        await toggle.ClickAsync();
        await toggle.ClickAsync();

        await page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.theme === 'dark'",
            null,
            new()
            {
                Timeout = 10_000
            });
        await page.WaitForSelectorAsync(".monaco-editor.vs-dark", 10);

        await page.ReloadAsync();
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        var theme = await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme");
        Assert.That(theme, Is.EqualTo("dark"), "theme should persist across reload");
        await page.WaitForSelectorAsync(".monaco-editor.vs-dark", 10);
    }

    // The Ctrl+Enter editor action runs the query without clicking Run.
    [Test]
    public async Task ExplorerRunViaKeyboard()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.EvaluateAsync(
            """
            () => {
                const editor = monaco.editor.getEditors()[0];
                editor.setValue('Query.Employee.ToList()');
                editor.focus();
            }
            """);
        await page.Keyboard.PressAsync("Control+Enter");

        await page.WaitForSelectorAsync("[data-testid='result-table']", 60);
        var table = await page.Locator("[data-testid='result-table']").InnerTextAsync();
        Assert.That(table, Does.Contain("Aaron"));
    }

    // Stage 3: invalid code surfaces Roslyn diagnostics as Monaco markers (editor squiggles).
    [Test]
    public async Task ExplorerDiagnostics()
    {
        var page = await Browser.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        // 'Nope' is not a member of the Employee model → a diagnostic marker should appear.
        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.Nope)");
        await page.WaitForFunctionAsync(
            "() => monaco.editor.getModelMarkers({}).length > 0",
            null,
            new()
            {
                Timeout = 30_000
            });

        var count = await page.EvaluateAsync<int>("() => monaco.editor.getModelMarkers({}).length");
        Assert.That(count, Is.GreaterThan(0));
    }
}
