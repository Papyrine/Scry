// Verify.Playwright snapshots of the two UIs the sample ships: the Blazor WASM client on /, and the
// Scry explorer on /scry. Verifying an IPage or an ILocator captures the rendered markup *and* a
// screenshot, so these guard what the UI looks like rather than only what it contains — the
// behavioural assertions live in UiSnapshotTests. Two of them are also the images readme.md and
// docs/explorer.md embed, which is why they are laid out at their own width — see the bottom of the
// file.
//
// Every page is opened at a fixed viewport: layout is what the screenshot is of, and a viewport that
// followed the machine would make every capture a different one. Subpixel text antialiasing is off (see
// BrowserFixture) because it did not reproduce between browser sessions even on one machine, and the
// pngs are compared with a tolerance rather than by byte (see PngComparer) because what survives that
// is a pixel or two at the end of a rule, rendered a shade lighter on one machine than on another. A
// baseline seeded here therefore holds on CI, which is what lets these run there at all.
[TestFixture]
[Category("Browser")]
public class UiScreenshotTests :
    BrowserFixture
{
    // Tall enough that the sample's four tables and the explorer's panes are captured without the
    // full-page stitching that a short viewport forces.
    static ViewportSize viewport = new()
    {
        Width = 1000,
        Height = 1200
    };

    // The two captures at the bottom of this file are the images the docs embed, and they are laid out
    // at 800 rather than the width above: a doc renderer shows them at native size, and scaling a wider
    // capture down to fit softens every glyph in it.
    static ViewportSize docsViewport = new()
    {
        Width = 800,
        Height = 1000
    };

    // Past the breakpoint in app.css the query and everything it produced sit side by side rather than
    // stacked. Every other capture here is narrower than that breakpoint, so this is the only one the
    // two-column layout appears in.
    static ViewportSize wideViewport = new()
    {
        Width = 1500,
        Height = 1000
    };

    // The IntelliSense capture is of the viewport rather than of the full page, so this height is its
    // crop — sized to the editor card and the dropdown over it, because a screen of empty space beneath
    // them is not what the doc is showing.
    static ViewportSize intelliSenseViewport = new()
    {
        Width = 800,
        Height = 660
    };

    [Test]
    public async Task SampleHomePage()
    {
        var page = await NewSizedPageAsync();
        await page.GotoAsync(BaseUrl);

        // All four tables have rendered and every attachment has been fetched, so the capture is of the
        // settled page rather than of one still filling in.
        await page.WaitForSelectorAsync("table tbody tr");
        await Assertions.Expect(page.Locator("table")).ToHaveCountAsync(4);
        await page.WaitForSelectorAsync("[data-testid='faces'][data-fetched='true']");

        await Verify(page)
            .PrettyPrintHtml();
    }

    // The employee table on its own: the projection, the em-dash for an absent manager, and the
    // nested department name, without the rest of the page moving underneath it.
    [Test]
    public async Task SampleEmployeeTable()
    {
        var page = await NewSizedPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForSelectorAsync("table tbody tr");

        await Verify(page.Locator("table").First)
            .PrettyPrintHtml();
    }

    // Screenshot only. The explorer's markup is dominated by Monaco, whose DOM carries generated ids
    // and measurement spans that differ run to run — UiSnapshotTests.ExplorerShellMarkup snapshots the
    // markup with those reduced away, so what is worth capturing here is the rendering.
    [Test]
    public async Task ExplorerShell()
    {
        var page = await NewSizedPageAsync();
        await GoToExplorer(page);

        await Verify(page)
            .PageScreenshotOptions(
                new()
                {
                    FullPage = true
                },
                screenshotOnly: true);
    }

    // The same shell in dark mode: the toggle retints Monaco and the page together, and a screenshot
    // is the only thing that shows they agree.
    [Test]
    public async Task ExplorerDarkMode()
    {
        var page = await NewSizedPageAsync();
        await GoToExplorer(page);

        // System → Light → Dark, so the result does not depend on the machine's own preference.
        var toggle = page.Locator("[data-testid='theme-toggle']");
        await toggle.ClickAsync();
        await toggle.ClickAsync();
        await page.WaitForSelectorAsync(".monaco-editor.vs-dark", 10);

        await Verify(page)
            .PageScreenshotOptions(
                new()
                {
                    FullPage = true
                },
                screenshotOnly: true);
    }

    // The shell wide enough for the split layout, with a query run so that both columns have something
    // in them: the editor and its history on the left, the wire request, rows and response on the right.
    [Test]
    public async Task ExplorerSideBySide()
    {
        var page = await NewSizedPageAsync(wideViewport);
        await GoToExplorer(page);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new { _.Name, _.Status })
            """);
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 60);
        await SettleScrollbarsAsync(page);

        await Verify(page)
            .PageScreenshotOptions(
                new()
                {
                    FullPage = true
                },
                screenshotOnly: true);
    }

    // A query run end to end, captured as the table the explorer renders from the server's response.
    [Test]
    public async Task ExplorerResultTable()
    {
        var page = await NewSizedPageAsync();
        await GoToExplorer(page);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new { _.Name, _.Status })
            """);
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 60);

        await Verify(page.Locator("[data-testid='result-table']"))
            .PrettyPrintHtml();
    }

    // The SQL pane. The server builds the query and reads its SQL back without executing it, so this
    // captures what EF produced for the LINQ the client wrote.
    [Test]
    public async Task ExplorerSqlPreview()
    {
        var page = await NewSizedPageAsync();
        await GoToExplorer(page);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .Select(_ => new { _.Name })
            """);
        await page.Locator("[data-testid='sql-preview']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='sql']", 30);

        await Verify(page.Locator("[data-testid='sql']"))
            .Snapshot(
                """
                <span class="tok-kw">SELECT</span> [e].[Name]
                <span class="tok-kw">FROM</span> [Employees] <span class="tok-kw">AS</span> [e]
                <span class="tok-kw">WHERE</span> [e].[Active] = <span class="tok-kw">CAST</span>(<span class="tok-num">1</span> <span class="tok-kw">AS</span> bit)
                """);
    }

    // The sidecar open over the sample, with one captured query selected — the capture
    // docs/sidecar.md embeds.
    [Test]
    public async Task SampleSidecar()
    {
        var page = await NewSizedPageAsync(docsViewport);
        await page.GotoAsync(BaseUrl);
        await page.WaitForSelectorAsync("table tbody tr");

        // Including the attachment fetches, which are the last exchanges the page makes and the ones
        // the panel below lists as ATTACHMENT rather than QUERY.
        await page.WaitForSelectorAsync("[data-testid='faces'][data-fetched='true']");

        await page.Keyboard.PressAsync("Alt+KeyQ");
        await page.WaitForSelectorAsync("[data-testid='sidecar-entries'] li", 10);
        await page.Locator("[data-testid='sidecar-entries'] li").First.ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='sidecar-request']", 10);

        // The panel's stylesheet is injected on first open; capture only once it has applied.
        await page.WaitForFunctionAsync(
            "() => Array.from(document.styleSheets).some(_ => _.href && _.href.includes('scry-sidecar'))");

        // Durations, the Date header, the ETag, and the schema stamp differ run to run; pin them so
        // the capture is of the layout rather than of one run's values.
        await page.EvaluateAsync(
            """
            () => {
                for (const cell of document.querySelectorAll('.scry-sidecar-duration')) {
                    cell.textContent = '1 ms';
                }

                for (const row of document.querySelectorAll('[data-testid=sidecar-response-headers] tr')) {
                    const key = row.querySelector('th')?.textContent?.toLowerCase();
                    if (key === 'date' || key === 'etag' || key === 'scry-schema-stamp') {
                        row.querySelector('td').textContent = '…';
                    }
                }
            }
            """);

        await Verify(page)
            .PageScreenshotOptions(
                new()
                {
                    FullPage = true
                },
                screenshotOnly: true);
    }

    // The captures the docs embed. readme.md and docs/explorer.md point their <img> straight at these
    // verified files, so a published screenshot cannot drift from the UI: a change to the explorer
    // fails the snapshot, and accepting the new baseline is what republishes the image.

    // Monaco's completion dropdown, listing exactly the allow-listed Employee members Roslyn resolved
    // from the introspected schema — and not the [QueryIgnore]d Salary.
    [Test]
    public async Task ExplorerIntelliSense()
    {
        var page = await NewSizedPageAsync(intelliSenseViewport);
        await GoToExplorer(page);

        // The caret goes to the end of the sample query ("…Where(_ => _."), which is where the member
        // list is worth showing, and is pinned solid first: a blinking caret is two different images
        // depending on when the shutter falls.
        await page.EvaluateAsync(
            """
            () => {
                const editor = monaco.editor.getEditors()[0];
                editor.updateOptions({ cursorBlinking: 'solid' });
                editor.focus();
                editor.setPosition({ lineNumber: 1, column: editor.getModel().getLineMaxColumn(1) });
                editor.trigger('docs', 'editor.action.triggerSuggest', {});
            }
            """);
        await page.WaitForSelectorAsync(".suggest-widget .monaco-list-row", 30);
        await SettleScrollbarsAsync(page);

        await Verify(page)
            .PageScreenshotOptions(new(), screenshotOnly: true);
    }

    // The whole pipeline on one screen: the LINQ as written, the wire request it translated to, the
    // rows the server returned, and the raw response envelope.
    [Test]
    public async Task ExplorerRun()
    {
        var page = await NewSizedPageAsync(docsViewport);
        await GoToExplorer(page);

        // Broken across lines so it sits inside the editor's width unwrapped: this is the capture that
        // shows the LINQ a caller writes, and a horizontal scrollbar over it shows nothing.
        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new { _.Name })
            """);
        await page.Locator("[data-testid='run']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='result-table'] tbody tr", 60);

        await SettleScrollbarsAsync(page);

        await Verify(page)
            .PageScreenshotOptions(
                new()
                {
                    FullPage = true
                },
                screenshotOnly: true);
    }

    // Monaco fades a scrollbar out once whatever it belongs to stops being touched — the editor's after
    // the query is set, the suggest widget's after the list opens. A capture taken mid-fade differs from
    // the last one by a column of part-transparent pixels, which is the whole of the difference between
    // two otherwise identical runs. The fade is not waited out but removed: Chromium runs an opacity
    // transition on the compositor, so polling getComputedStyle reads it as finished while the painted
    // pixels are still moving — three runs of the same capture read that column as three different
    // greys. Dropping the transition snaps every scrollbar straight to the state it was heading for, so
    // the capture is of the settled UI whenever the shutter falls.
    static Task SettleScrollbarsAsync(IPage page) =>
        page.AddStyleTagAsync(
            new()
            {
                Content =
                    """
                    .monaco-scrollable-element > .scrollbar,
                    .monaco-scrollable-element > .scrollbar > .slider {
                        transition: none !important;
                    }
                    """
            });

    // Through the fixture's factory rather than the browser directly, so a screenshot page records
    // what it logs exactly as a snapshot page does.
    Task<IPage> NewSizedPageAsync(ViewportSize? size = null) =>
        NewPageAsync(
            new()
            {
                ViewportSize = size ?? viewport
            });

    // Boots the explorer far enough to be worth capturing: Monaco mounted and the schema loaded, so the
    // shell in the image is the settled one rather than one still filling in.
    Task GoToExplorer(IPage page) =>
        page.GoToExplorerAsync(BaseUrl);
}
