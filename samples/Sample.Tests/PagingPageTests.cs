using Bunit;
using PagingPage = Sample.WebClient.Pages.Paging;

// Renders the real Paging page against the real Scry server pipeline (in-memory) and drives the
// Next button, proving ToPageAsync + HasMore page through the seeded employees end to end.
[NotInParallel]
public class PagingPageTests
{
    [Test]
    public async Task PagesThroughEmployees()
    {
        var server = await SharedScryServer.InstanceAsync();

        await using var context = new BunitContext();
        context.Services.AddSingleton(server.CreateScryClient());
        context.Services.AddSingleton<ScryQuery>();

        var page = context.Render<PagingPage>();
        await page.WaitForStateAsync(
            () => page.FindAll("tbody tr").Count > 0,
            TimeSpan.FromSeconds(10));

        // Re-read the DOM fresh each time so a post-click re-render is reflected.
        string[] Names() => [.. page.FindAll("tbody tr td:first-child").Select(_ => _.TextContent)];

        string[] firstPage = ["Aaron", "Alice"];
        string[] secondPage = ["Bob", "Carol"];

        // Page 1 — ordered by Name: Aaron, Alice — with a further page available.
        await Assert.That(Names()).IsEquivalentTo(firstPage, CollectionOrdering.Matching);
        await Assert.That(page.FindAll("button")[1].HasAttribute("disabled")).IsFalse().Because("Next enabled on page 1");
        await Assert.That(page.FindAll("button")[0].HasAttribute("disabled")).IsTrue().Because("Previous disabled on page 1");

        await page.FindAll("button")[1].ClickAsync();
        await page.WaitForStateAsync(
            () => Names().FirstOrDefault() == "Bob",
            TimeSpan.FromSeconds(10));

        // Page 2 — Bob, Carol — the last page, so Next is now disabled and Previous enabled.
        await Assert.That(Names()).IsEquivalentTo(secondPage, CollectionOrdering.Matching);
        await Assert.That(page.FindAll("button")[1].HasAttribute("disabled")).IsTrue().Because("Next disabled on last page");
        await Assert.That(page.FindAll("button")[0].HasAttribute("disabled")).IsFalse().Because("Previous enabled on page 2");
    }
}
