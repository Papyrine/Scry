/// <summary>
/// The /commands page in a real browser, against the real server and its handlers: WebAssembly, the
/// browser's fetch reading a command's streamed receipts as they arrive, and the pending-work panel
/// following a command the client stopped waiting for. Plus the two captures the docs embed.
/// </summary>
/// <remarks>
/// A fixture of its own, since these write, and the other browser fixtures capture the seed. Each test
/// works on rows it hired; the one capture of the whole table runs first, before anything is hired.
/// A slow rename takes the sample's five seconds — past the client's three-second wait, which is what
/// puts it in the panel at all.
/// </remarks>
[TestFixture]
[Category("Browser")]
public class CommandUiTests :
    BrowserFixture
{
    [Test]
    [Order(1)]
    public async Task SampleCommands()
    {
        var page = await NewSizedPageAsync();
        await Open(page);

        // The body rather than the page: a page holding a live query never goes network-idle.
        await Verify(page.Locator("body"))
            .PrettyPrintHtml();
    }

    [Test]
    public async Task HiringAnswersInline()
    {
        var page = await NewPageAsync();
        await Open(page);

        var name = await Hire(page, "Inline");

        var id = await Row(page, name).GetAttributeAsync("data-id");
        await Assertions.Expect(page.Locator("[data-testid=command-status]")).ToHaveTextAsync($"Hired {name} as #{id}.");
        await Assertions.Expect(page.Locator("[data-testid=pending-work]")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task DeactivatingARowLetsItBeDeleted()
    {
        var page = await NewPageAsync();
        await Open(page);
        var name = await Hire(page, "Leaver");
        var row = Row(page, name);
        await Assertions.Expect(row.Locator(".delete")).ToBeDisabledAsync();

        await row.Locator(".toggle").ClickAsync();
        await Assertions.Expect(row.Locator(".delete")).ToBeEnabledAsync();
        await row.Locator(".delete").ClickAsync();

        await Assertions.Expect(row).ToHaveCountAsync(0);
    }

    [Test]
    public async Task ASlowRenameWaitsInThePanelUntilItLands()
    {
        var page = await NewPageAsync();
        await Open(page);
        var name = await Hire(page, "Patient");
        var renamed = $"{name} slowly";
        await page.FillAsync("#rename-to", renamed);

        await Row(page, name).Locator(".rename").ClickAsync();

        var item = page.Locator("[data-testid=pending-work] li[data-command=RenameEmployee]");
        await Assertions.Expect(item).ToHaveAttributeAsync("data-status", "pending", new() {Timeout = 15_000});
        await Assertions.Expect(item).ToHaveAttributeAsync("data-status", "done", new() {Timeout = 15_000});
        await Assertions.Expect(Row(page, renamed)).ToHaveCountAsync(1);
    }

    // Two browser contexts are two clients, neither told about the other: the page watching hears of
    // the rename from the server, because the rows its query reads changed.
    [Test]
    public async Task ARenameInOneBrowserReachesAnother()
    {
        var watching = await NewPageInAsync(await NewContextAsync());
        await Open(watching);
        var name = await Hire(watching, "Watched");

        var renaming = await NewPageInAsync(await NewContextAsync());
        await Open(renaming);
        await renaming.FillAsync("#rename-to", $"{name} elsewhere");
        await Row(renaming, name).Locator(".rename").ClickAsync();

        await Assertions.Expect(Row(watching, $"{name} elsewhere")).ToHaveCountAsync(1);
    }

    // The panel as the docs show it: a rename still running, past the client's wait.
    [Test]
    public async Task SamplePendingWork()
    {
        var page = await NewSizedPageAsync(docsViewport);
        await Open(page);
        var name = await Hire(page, "Pictured");
        await page.FillAsync("#rename-to", $"{name} slowly");
        await Row(page, name).Locator(".rename").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=pending-work] li[data-status=pending]")).ToHaveCountAsync(1, new() {Timeout = 15_000});
        await page.WaitForFunctionAsync(
            "() => Array.from(document.styleSheets).some(_ => _.href && _.href.includes('scry-commands'))");

        // How long it has been running differs run to run, as does the row's id; pin both so the
        // capture is of the panel.
        await page.EvaluateAsync(
            """
            () => {
                for (const cell of document.querySelectorAll('.scry-commands-elapsed')) {
                    cell.textContent = '3.2s';
                }

                for (const cell of document.querySelectorAll('.scry-commands-name')) {
                    cell.textContent = 'RenameEmployee · Employee 5';
                }
            }
            """);

        await Verify(page.Locator("[data-testid=pending-work]"))
            .LocatorScreenshotOptions(new(), screenshotOnly: true);
    }

    static int hires;

    // As the other captures are sized: the page at the width the sample reads at, and the panel at the
    // width the docs render it at natively.
    static ViewportSize viewport = new()
    {
        Width = 1000,
        Height = 900
    };

    static ViewportSize docsViewport = new()
    {
        Width = 800,
        Height = 700
    };

    Task<IPage> NewSizedPageAsync(ViewportSize? size = null) =>
        NewPageAsync(
            new()
            {
                ViewportSize = size ?? viewport
            });

    // Waits for the table and the capabilities both, which is what the page marks as ready.
    async Task Open(IPage page)
    {
        await page.GotoAsync($"{BaseUrl}/commands");
        await page.WaitForSelectorAsync("#employees[data-ready='true']");
    }

    static async Task<string> Hire(IPage page, string prefix)
    {
        var name = $"{prefix} {Interlocked.Increment(ref hires)}";
        await page.FillAsync("#create-name", name);
        await page.ClickAsync("#create-submit");
        await Assertions.Expect(Row(page, name)).ToHaveCountAsync(1);
        return name;
    }

    static ILocator Row(IPage page, string name) =>
        page.Locator($"#employees tbody tr:has(td.name:text-is(\"{name}\"))");
}
