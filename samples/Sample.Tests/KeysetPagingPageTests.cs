using Bunit;
using KeysetPage = Sample.Client.Pages.KeysetPaging;

// Renders the real Keyset paging page against the real Scry server pipeline (in-memory) and drives the
// Next button, proving cursor round-tripping (page 1 emits a cursor, Next resumes past it) end to end.
[TestFixture]
public class KeysetPagingPageTests
{
    [Test]
    public async Task PagesThroughEmployeesByCursor()
    {
        var server = await SharedScryServer.InstanceAsync();

        await using var context = new BunitContext();
        context.Services.AddSingleton(server.CreateScryClient());
        context.Services.AddSingleton<ScryQuery>();

        var page = context.Render<KeysetPage>();
        await page.WaitForStateAsync(
            () => page.FindAll("tbody tr").Count > 0,
            TimeSpan.FromSeconds(10));

        string[] names() => [.. page.FindAll("tbody tr td:first-child").Select(_ => _.TextContent)];

        string[] firstPage = ["Aaron", "Alice"];
        string[] secondPage = ["Bob", "Carol"];

        // Page 1 — Aaron, Alice — with a further page reachable by cursor.
        Assert.That(names(), Is.EqualTo(firstPage));
        Assert.That(page.FindAll("button")[0].HasAttribute("disabled"), Is.False, "Next enabled on page 1");

        page.FindAll("button")[0].Click();
        await page.WaitForStateAsync(
            () => names().FirstOrDefault() == "Bob",
            TimeSpan.FromSeconds(10));

        // Page 2 — Bob, Carol — the last page, so Next is disabled (no cursor to resume from).
        Assert.That(names(), Is.EqualTo(secondPage));
        Assert.That(page.FindAll("button")[0].HasAttribute("disabled"), Is.True, "Next disabled on last page");
    }
}
