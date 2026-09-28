using Bunit;
using KeysetPage = Sample.WebClient.Pages.KeysetPaging;

// Renders the real Keyset paging page against the real Scry server pipeline (in-memory) and drives the
// Next button, proving cursor round-tripping (page 1 emits a cursor, Next resumes past it) end to end.
[NotInParallel]
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

        string[] Names() => [.. page.FindAll("tbody tr td:first-child").Select(_ => _.TextContent)];

        string[] firstPage = ["Aaron", "Alice"];
        string[] secondPage = ["Bob", "Carol"];

        // Page 1 — Aaron, Alice — with a further page reachable by cursor.
        await Assert.That(Names()).IsEquivalentTo(firstPage, CollectionOrdering.Matching);
        await Assert.That(page.FindAll("button")[0].HasAttribute("disabled")).IsFalse().Because("Next enabled on page 1");

        await page.FindAll("button")[0].ClickAsync();
        await page.WaitForStateAsync(
            () => Names().FirstOrDefault() == "Bob",
            TimeSpan.FromSeconds(10));

        // Page 2 — Bob, Carol — the last page, so Next is disabled (no cursor to resume from).
        await Assert.That(Names()).IsEquivalentTo(secondPage, CollectionOrdering.Matching);
        await Assert.That(page.FindAll("button")[0].HasAttribute("disabled")).IsTrue().Because("Next disabled on last page");
    }
}
