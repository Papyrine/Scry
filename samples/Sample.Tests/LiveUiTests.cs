/// <summary>
/// The /live pages in a real browser, against the real server: WebAssembly, the browser's own fetch
/// under HttpClient, and a response that never ends being read as it arrives. What the in-process
/// page tests cannot show is that last part — a browser that held the response until it completed
/// would render nothing at all.
/// </summary>
[TestFixture]
[Category("Browser")]
public class LiveUiTests :
    BrowserFixture
{
    const string firstAmount = "#orders tbody tr:first-child td:last-child";

    // Two pages are two clients. Neither is told about the other: the one watching hears of the
    // write from the server, because the rows its query reads changed.
    [Test]
    public async Task APageHearsOfAWriteMadeFromAnother()
    {
        var watching = await NewPageAsync();
        await watching.GotoAsync($"{BaseUrl}/live");
        var before = await Amount(watching, firstAmount);

        var writing = await NewPageAsync();
        await writing.GotoAsync($"{BaseUrl}/live/stream");
        await Amount(writing, firstAmount);
        await writing.ClickAsync("#reprice");

        await Becomes(watching, firstAmount, before + 1);
        await Becomes(writing, firstAmount, before + 1);
    }

    [TestCase("/live")]
    [TestCase("/live/stream")]
    [TestCase("/live/messagepipe")]
    public async Task EachWayOfConsumingOneShowsTheChange(string path)
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}{path}");
        var before = await Amount(page, firstAmount);

        await page.ClickAsync("#reprice");

        await Becomes(page, firstAmount, before + 1);
    }

    // A bulk update the interceptor never sees, reported by the host instead.
    [Test]
    public async Task AWriteOnlyTheHostCouldReportArrivesToo()
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/live");
        var before = await Amount(page, firstAmount);

        await page.ClickAsync("#reprice-bulk");

        await Becomes(page, firstAmount, before + 1);
    }

    [Test]
    public async Task RxFoldsTheChangeOutOfTwoAnswers()
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/live/rx");
        var before = await Amount(page, "#total");

        await page.ClickAsync("#reprice");

        await Becomes(page, "#total", before + 1);
        Assert.That(await page.InnerTextAsync("#change"), Is.EqualTo("+1.00"));
    }

    // The same page over a hub connection instead of HTTP. Nothing on it changes but the transport —
    // which is the point — so what is pinned is that it goes on working: the rows come back over the
    // socket, and a write still arrives without being asked for.
    [TestCase("/live")]
    [TestCase("/live/stream")]
    public async Task TheSamePageWorksOverAHub(string path)
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}{path}");
        await Amount(page, firstAmount);

        await page.CheckAsync("#transport-signalr");
        await Assertions.Expect(page.Locator("#transport")).ToHaveAttributeAsync("data-transport", "signalr");
        var before = await Amount(page, firstAmount);

        await page.ClickAsync("#reprice");

        await Becomes(page, firstAmount, before + 1);
    }

    // Leaving a live page ends its subscriptions, so a visitor clicking through the tabs does not
    // pile them up: each page works, in turn, in the one browser tab.
    [Test]
    public async Task TheTabsCanBeWalkedInOnePage()
    {
        var page = await NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/live");
        await Amount(page, firstAmount);

        foreach (var tab in new[] {"Stream", "MessagePipe", "Callback"})
        {
            await page.ClickAsync($"nav.tabs >> text={tab}");
            var before = await Amount(page, firstAmount);
            await page.ClickAsync("#reprice");
            await Becomes(page, firstAmount, before + 1);
        }
    }

    // Waits for the cell to exist, which is the first answer arriving, and reads it.
    static async Task<decimal> Amount(IPage page, string selector)
    {
        await page.WaitForSelectorAsync(selector);
        return decimal.Parse(await page.InnerTextAsync(selector), CultureInfo.InvariantCulture);
    }

    static Task Becomes(IPage page, string selector, decimal expected) =>
        page.WaitForFunctionAsync(
            "([selector, expected]) => parseFloat(document.querySelector(selector)?.innerText) === expected",
            new object[] {selector, (double)expected});
}
