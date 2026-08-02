// Verify.Playwright snapshots of the two UIs the sample ships: the Blazor WASM client on /, and the
// Scry explorer on /scry. Verifying an IPage or an ILocator captures the rendered markup *and* a
// screenshot, so these guard what the UI looks like rather than only what it contains — the
// behavioural assertions live in UiSnapshotTests.
//
// Every page is opened at a fixed viewport: layout is what the screenshot is of, and a viewport that
// followed the machine would make every capture a different one. Subpixel text antialiasing is off
// (see BrowserFixture) because it did not reproduce between browser sessions even on one machine; what
// remains is the platform's own font stack, so a first run on a new OS or CI image is still expected to
// need reseeding.
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

    [Test]
    public async Task SampleHomePage()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(BaseUrl);

        // All four tables have rendered, so the capture is of the settled page rather than of one
        // still filling in.
        await page.WaitForSelectorAsync("table tbody tr");
        await Assertions.Expect(page.Locator("table")).ToHaveCountAsync(4);

        await Verify(page)
            .PrettyPrintHtml();
    }

    // The employee table on its own: the projection, the em-dash for an absent manager, and the
    // nested department name, without the rest of the page moving underneath it.
    [Test]
    public async Task SampleEmployeeTable()
    {
        var page = await NewPageAsync();
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
        var page = await NewPageAsync();
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
        var page = await NewPageAsync();
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

    // A query run end to end, captured as the table the explorer renders from the server's response.
    [Test]
    public async Task ExplorerResultTable()
    {
        var page = await NewPageAsync();
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
        var page = await NewPageAsync();
        await GoToExplorer(page);

        await page.SetEditorValueAsync(
            """
            Query.Employee
                .Where(_ => _.Active)
                .Select(_ => new { _.Name })
            """);
        await page.Locator("[data-testid='sql-preview']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='sql']", 30);

        await Verify(page.Locator("[data-testid='sql']"));
    }

    Task<IPage> NewPageAsync() =>
        Browser.NewPageAsync(
            new()
            {
                ViewportSize = viewport
            });

    // Boots the explorer far enough to be worth capturing: Monaco mounted, and the schema loaded —
    // which the auto-run completion list appearing is the signal for. Roslyn's first completion in the
    // WASM interpreter is slow on a cold load, hence the long wait.
    async Task GoToExplorer(IPage page)
    {
        await page.GotoAsync($"{BaseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("[data-testid='completions'] li", 90);
    }
}
