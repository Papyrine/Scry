using System.Buffers.Text;

/// <summary>
/// The debug sidecar over the running sample: toggled by its shortcut, populated by the page's own
/// queries, and linking into the explorer with the captured query pre-populated.
/// </summary>
[TestFixture]
[Category("Browser")]
public class SidecarUiTests :
    BrowserFixture
{
    [Test]
    public async Task TogglesWithTheShortcut()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForSelectorAsync("table tbody tr", 30);

        await page.Keyboard.PressAsync("Alt+KeyQ");
        await page.WaitForSelectorAsync("[data-testid='sidecar']", 10);

        await page.Keyboard.PressAsync("Alt+KeyQ");
        await Assertions.Expect(page.Locator("[data-testid='sidecar']")).ToHaveCountAsync(0);
    }

    // The clickable way in: the floating button opens the panel, and the panel's own Close button
    // brings the launcher back.
    [Test]
    public async Task TogglesWithTheButton()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForSelectorAsync("table tbody tr", 30);

        await page.Locator("[data-testid='sidecar-toggle']").ClickAsync();
        await page.WaitForSelectorAsync("[data-testid='sidecar']", 10);
        await Assertions.Expect(page.Locator("[data-testid='sidecar-toggle']")).ToHaveCountAsync(0);

        await page.Locator("[data-testid='sidecar-close']").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='sidecar']")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("[data-testid='sidecar-toggle']")).ToHaveCountAsync(1);
    }

    [Test]
    public async Task CapturesThePagesQueries()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForSelectorAsync("table tbody tr", 30);

        await page.Keyboard.PressAsync("Alt+KeyQ");
        await page.WaitForSelectorAsync("[data-testid='sidecar-entries'] li", 10);

        // The home page fills four tables, each from its own query.
        var rows = page.Locator("[data-testid='sidecar-entries'] li");
        Assert.That(await rows.CountAsync(), Is.GreaterThanOrEqualTo(4));
        await Assertions.Expect(rows.First).ToContainTextAsync("GET");
        await Assertions.Expect(rows.First).ToContainTextAsync("200");

        // Selecting an entry shows the decoded request — the GET URL's q parameter, made readable.
        await rows.First.ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='sidecar-request']")).ToContainTextAsync("\"root\"");
        await Assertions.Expect(page.Locator("[data-testid='sidecar-response']")).ToContainTextAsync("\"kind\"");
        // The browser's fetch reports header names lowercased.
        await Assertions.Expect(page.Locator("[data-testid='sidecar-response-headers']"))
            .ToContainTextAsync(WireFormat.SchemaStampHeader.ToLowerInvariant());
    }

    // The deep link is the explorer's own share format: the wire request rendered back into C#,
    // base64url in the fragment. Opening it lands in the explorer with the editor pre-populated.
    [Test]
    public async Task ExplorerLinkPrepopulatesTheQuery()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(BaseUrl);
        await page.WaitForSelectorAsync("table tbody tr", 30);

        await page.Keyboard.PressAsync("Alt+KeyQ");
        await page.WaitForSelectorAsync("[data-testid='sidecar-entries'] li", 10);
        await page.Locator("[data-testid='sidecar-entries'] li").First.ClickAsync();

        var href = await page.Locator("[data-testid='sidecar-explorer-link']").GetAttributeAsync("href");
        Assert.That(href, Does.StartWith("/scry/#q="));

        var encoded = href!["/scry/#q=".Length..];
        var snippet = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded));
        Assert.That(snippet, Does.StartWith("Query."));

        // The link is the explorer's tested entry point: opening it fills the editor with the snippet.
        await page.GotoAsync($"{BaseUrl}{href}");
        await page.WaitForSelectorAsync(".monaco-editor", 30);
        await page.WaitForSelectorAsync("main[data-ready]", 90);
        var value = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
        Assert.That(value, Is.EqualTo(snippet));
    }
}
