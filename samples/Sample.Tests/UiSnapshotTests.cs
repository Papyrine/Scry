// Launches the real Sample.Server (the same DLL `dotnet run` would execute) and drives it with a
// headless Chromium browser, snapshotting the live WebAssembly UI as HTML + a screenshot.
// Categorised "Browser" so CI can opt out (screenshots are environment sensitive, and the browser
// download / WASM boot is heavier than the in-process tests).
[TestFixture]
[Category("Browser")]
public class UiSnapshotTests
{
    Process server = null!;
    IPlaywright playwright = null!;
    IBrowser browser = null!;
    string baseUrl = null!;
    string workDir = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var port = GetFreePort();
        baseUrl = $"http://127.0.0.1:{port}";

        // Run the server from a throwaway working directory so nothing it writes lands in the repo.
        workDir = Directory.CreateTempSubdirectory("scry_ui_").FullName;

        server = new()
        {
            StartInfo =
            {
                FileName = "dotnet",
                WorkingDirectory = workDir,
                UseShellExecute = false
            }
        };
        server.StartInfo.ArgumentList.Add(LocateServerDll());
        server.StartInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        // Development so the (Development-only) Scry explorer is reachable; the server's explicit
        // UseStaticWebAssets() call means the WASM client is served in this environment too.
        server.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        server.Start();

        await WaitForServer(port);

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        playwright?.Dispose();

        if (server is { HasExited: false })
        {
            server.Kill(entireProcessTree: true);
            // Give the process a moment to exit before the working directory is removed.
            server.WaitForExit(milliseconds: 5000);
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        server?.Dispose();

        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup of a temp directory; a lingering file lock is not a test failure.
        }
    }

    [Test]
    public async Task HomePage()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync(baseUrl);

        // Wait until the WebAssembly app has run its queries and rendered the result tables.
        await page.WaitForSelectorAsync("table tbody tr");
        await Assertions.Expect(page.Locator("table")).ToHaveCountAsync(4);

        // Snapshot the real WebAssembly-rendered DOM (not a screenshot): pixel screenshots differ
        // across machines/OS font rendering and can't run in CI, but the rendered markup is stable.
        var rendered = await page.Locator("#app").InnerHTMLAsync();
        await Verify(rendered);
    }

    // The explorer fetches GET {route}/introspect on load to learn the queryable schema. Exercises the
    // endpoint end-to-end (routing, Development guard, ScryJson serialization) against the live server.
    [Test]
    public async Task ExplorerIntrospectionEndpoint()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"{baseUrl}/scry/introspect");

        await Verify(json);
    }

    // Verifies the Scry explorer (a separate Blazor WASM app, embedded in and served by the
    // Scry.Server.Explorer package under /scry) boots from the embedded assets in a real browser.
    [Test]
    public async Task ExplorerBoots()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");

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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");

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
        var page = await browser.NewPageAsync();

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

        await page.GotoAsync($"{baseUrl}/scry");

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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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

    // The SQL pane: the server builds the query and reads its SQL back without executing it. The
    // sample server runs in Development, which is what the preview's own guard defaults to.
    [Test]
    public async Task ExplorerShowsSql()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name })");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        const string query = "Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name })";
        await page.SetEditorValueAsync(query);
        await page.Locator("[data-testid='share']").ClickAsync();

        var shared = await page.EvaluateAsync<string>("() => location.href");
        Assert.That(shared, Does.Contain("#q="));

        // A fresh load of the shared link, not a fragment change on the running app: a hash-only
        // navigation would leave the editor as it is and prove nothing.
        var opened = await browser.NewPageAsync();
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry/#q=!!!not-base64!!!");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForFunctionAsync(
            "() => monaco.editor.getEditors().length > 0 && monaco.editor.getEditors()[0].getValue().length > 0",
            null,
            new() {Timeout = 30_000});

        var value = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");

        Assert.That(value, Does.StartWith("Query.Employee.Where"));
        Assert.That(await page.Locator("[data-testid='error']").CountAsync(), Is.Zero);
    }

    // The result table exports as CSV. The download itself is the browser's, so the test intercepts
    // the interop call and asserts the payload the UI produced.
    [Test]
    public async Task ExplorerExportsResultsAsCsv()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name, _.Status })");
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 30);

        await page.EvaluateAsync(
            """
            () => {
                window.__csv = null;
                window.scry.download = (name, text, type) => window.__csv = { name, text, type };
            }
            """);
        await page.Locator("[data-testid='csv']").ClickAsync();
        await page.WaitForFunctionAsync("() => window.__csv !== null", null, new() {Timeout = 30_000});

        var name = await page.EvaluateAsync<string>("() => window.__csv.name");
        var text = await page.EvaluateAsync<string>("() => window.__csv.text");

        Assert.That(name, Is.EqualTo("scry-result.csv"));
        Assert.That(text, Does.StartWith("name,status"));
        Assert.That(text, Does.Contain("Alice,FullTime"));
    }

    // Proves the inline Monaco IntelliSense dropdown is wired to the Roslyn provider.
    [Test]
    public async Task ExplorerInlineSuggestions()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        // Set a complete query, then run it.
        await page.SetEditorValueAsync(
            "Query.Employee.Where(_ => _.Active).OrderBy(_ => _.Name).Select(_ => new { _.Name, _.Status })");
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

        // Stage 3: the executed query is recorded in history.
        await page.WaitForSelectorAsync("[data-testid='history'] li", 10);
        var historyText = await page.Locator("[data-testid='history']").InnerTextAsync();
        Assert.That(historyText, Does.Contain("Query.Employee.Where"));
    }

    // Terminal support: a plain LINQ '.ToList()' (the habitual way to ask for all rows) is folded into
    // an enumerate-all list request rather than enumerating synchronously (which would deadlock WASM).
    [Test]
    public async Task ExplorerRunToList()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.Active).CountAsync()");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);

        await page.SetEditorValueAsync(
            "Query.Employee.Where(_ => _.Active).OrderBy(_ => _.Name).Select(_ => new { _.Name, _.Status }).FirstAsync()");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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

    // Full interactive walkthrough with screenshots (manual/local verification, not part of CI).
    [Test, Explicit]
    public async Task ExplorerWalkthrough()
    {
        var dir = Directory.CreateTempSubdirectory("scry_walk_").FullName;
        var log = new List<string>();
        var page = await browser.NewPageAsync(
            new()
            {
                // 800 wide because that is the width the committed images are: laying the page out at
                // the target width renders its text at native size, where scaling a wider capture down
                // to fit would soften every glyph in it.
                ViewportSize = new()
                {
                    Width = 800,
                    Height = 1000
                }
            });
        var consoleErrors = new List<string>();
        page.Console +=
            (_, m) =>
            {
                if (m.Type == "error")
                {
                    consoleErrors.Add(m.Text);
                }
            };

        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);
        log.Add("✓ editor booted, schema loaded");
        await page.ScreenshotAsync(
            new()
            {
                Path = Path.Combine(dir, "1-loaded.png"),
                FullPage = true
            });

        // Inline IntelliSense dropdown.
        await page.EvaluateAsync(
            """
            () => {
                const editor = monaco.editor.getEditors()[0];
                editor.setValue('Query.Employee.Where(_ => _.');
                editor.focus();
                editor.setPosition({ lineNumber: 1, column: editor.getModel().getLineMaxColumn(1) });
                editor.trigger('t', 'editor.action.triggerSuggest', {});
            }
            """);
        await page.WaitForSelectorAsync(".suggest-widget .monaco-list-row", 20);
        await page.ScreenshotAsync(
            new()
            {
                Path = Path.Combine(dir, "2-intellisense.png")
            });
        var suggest = await page.Locator(".suggest-widget .monaco-list-row").AllInnerTextsAsync();
        log.Add($"✓ IntelliSense dropdown: {string.Join(", ", suggest)}");
        await page.Keyboard.PressAsync("Escape");

        // Run a complete query. Kept short enough to fit the editor's width unscrolled, since this is
        // the capture the docs use to show the LINQ a caller writes.
        await page.SetEditorValueAsync(
            "Query.Employee.Where(_ => _.Active).OrderBy(_ => _.Name).Select(_ => new { _.Name })");
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table']", 60);
        await page.ScreenshotAsync(
            new()
            {
                Path = Path.Combine(dir, "3-run.png"),
                FullPage = true
            });
        var wire = await page.Locator("[data-testid='wire']").InnerTextAsync();
        var table = await page.Locator("[data-testid='result-table']").InnerTextAsync();
        log.Add($"✓ run: wireHasEmployee={wire.Contains("Employee")}, table=[{table.Replace("\n", " ").Trim()}]");

        // Run a terminal query (scalar count) and capture the scalar rendering.
        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.Active).CountAsync()");
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-scalar']", 60);
        await page.ScreenshotAsync(
            new()
            {
                Path = Path.Combine(dir, "3b-count.png"),
                FullPage = true
            });
        var countWire = await page.Locator("[data-testid='wire']").InnerTextAsync();
        var countScalar = await page.Locator("[data-testid='result-scalar']").InnerTextAsync();
        log.Add($"✓ count terminal: wireHasCount={countWire.Contains("\"count\"")}, scalar={countScalar.Trim()}");

        // Toggle to dark mode (System → Light → Dark) and capture it.
        await page.Locator("[data-testid='theme-toggle']").ClickAsync();
        await page.Locator("[data-testid='theme-toggle']").ClickAsync();
        await page.WaitForSelectorAsync(".monaco-editor.vs-dark", 10);
        await page.ScreenshotAsync(
            new()
            {
                Path = Path.Combine(dir, "5-dark.png"),
                FullPage = true
            });
        var dataTheme = await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme");
        log.Add($"✓ dark mode: dataTheme={dataTheme}");

        // Diagnostics.
        await page.SetEditorValueAsync("Query.Employee.Where(_ => _.Nope)");
        await page.WaitForFunctionAsync(
            "() => monaco.editor.getModelMarkers({}).length > 0",
            null,
            new()
            {
                Timeout = 20_000
            });
        var markerMsg = await page.EvaluateAsync<string>(
            "() => monaco.editor.getModelMarkers({}).map(m => m.message).join(' | ')");
        log.Add($"✓ diagnostics: {markerMsg}");

        // Hover.
        await page.EvaluateAsync(
            """
            () => {
                const editor = monaco.editor.getEditors()[0];
                editor.setValue('Query.Employee.Where(_ => _.Active)');
                editor.layout();
            }
            """);
        await Task.Delay(500);
        var pt = await page.EvaluateAsync<float[]>(
            """
            () => {
                const editor = monaco.editor.getEditors()[0];
                const start = editor.getScrolledVisiblePosition({ lineNumber: 1, column: 29 });
                const end = editor.getScrolledVisiblePosition({ lineNumber: 1, column: 35 });
                const bounds = editor.getDomNode().getBoundingClientRect();
                return [
                    bounds.left + (start.left + end.left) / 2,
                    bounds.top + start.top + start.height / 2
                ];
            }
            """);
        await page.Mouse.MoveAsync(pt[0] - 80, pt[1]);
        await page.Mouse.MoveAsync(pt[0], pt[1], new() {Steps = 10});
        await Task.Delay(1800);
        await page.ScreenshotAsync(
            new()
            {
                Path = Path.Combine(dir, "4-hover.png")
            });
        var hoverCount = await page.Locator(".monaco-hover:not(.hidden)").CountAsync();
        // A fully laid-out editor has several .monaco-hover widgets (glyph-margin + content), so the
        // locator is not strict-safe — read the first one's text purely for the diagnostic log.
        var hoverWidgets = page.Locator(".monaco-hover");
        string hoverText;
        if (await hoverWidgets.CountAsync() > 0)
        {
            hoverText = await hoverWidgets.First.InnerTextAsync();
        }
        else
        {
            hoverText = "(no widget)";
        }

        log.Add($"{(hoverCount > 0 ? "✓" : "✗")} hover: visible={hoverCount}, text=[{hoverText.Replace('\n', ' ').Trim()}]");

        log.Add($"console errors: {(consoleErrors.Count == 0 ? "none" : string.Join(" || ", consoleErrors))}");
        await File.WriteAllLinesAsync(Path.Combine(dir, "results.txt"), log);
        await TestContext.Out.WriteLineAsync($"Walkthrough screenshots + results: {dir}");
        await TestContext.Out.WriteLineAsync(string.Join('\n', log));

        // Assert the headlessly-verifiable features. Hover needs a real browser (Monaco renders the
        // editor text clipped headless, so the mouse can't land on a token to trigger it).
        Assert.That(suggest, Does.Contain("Active"), "IntelliSense dropdown");
        Assert.That(wire, Does.Contain("Employee"), "wire request");
        Assert.That(table, Does.Contain("Aaron"), "result table");
        Assert.That(markerMsg, Does.Contain("Nope"), "diagnostics");
    }

    // Stage 3: invalid code surfaces Roslyn diagnostics as Monaco markers (editor squiggles).
    [Test]
    public async Task ExplorerDiagnostics()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
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

    static string LocateServerDll()
    {
        // .../samples/Sample.Tests/bin/<config>/<tfm>/ — mirror <config>/<tfm> onto the server output.
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = baseDir.Name;
        var config = baseDir.Parent!.Name;

        var dir = baseDir;
        while (dir is not null &&
               !Directory.Exists(Path.Combine(dir.FullName, "Sample.Server")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException(
                "Could not locate the Sample.Server project from the test output directory.");
        }

        var dll = Path.Combine(dir.FullName, "Sample.Server", "bin", config, tfm, "Sample.Server.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("Sample.Server build output not found; build the sample first.", dll);
        }

        return dll;
    }

    static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static async Task WaitForServer(int port)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"Sample.Server did not start listening on port {port}.");
    }
}
