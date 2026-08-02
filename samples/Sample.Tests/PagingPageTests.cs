using Bunit;
using PagingPage = Sample.Client.Pages.Paging;

// Renders the real Paging page against the real Scry server pipeline (in-memory) and drives the
// Next button, proving ToPageAsync + HasMore page through the seeded employees end to end.
[TestFixture]
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
        string[] names() => [.. page.FindAll("tbody tr td:first-child").Select(_ => _.TextContent)];

        string[] firstPage = ["Aaron", "Alice"];
        string[] secondPage = ["Bob", "Carol"];

        // Page 1 — ordered by Name: Aaron, Alice — with a further page available.
        Assert.That(names(), Is.EqualTo(firstPage));
        Assert.That(page.FindAll("button")[1].HasAttribute("disabled"), Is.False, "Next enabled on page 1");
        Assert.That(page.FindAll("button")[0].HasAttribute("disabled"), Is.True, "Previous disabled on page 1");

        await page.FindAll("button")[1].ClickAsync();
        await page.WaitForStateAsync(
            () => names().FirstOrDefault() == "Bob",
            TimeSpan.FromSeconds(10));

        // Page 2 — Bob, Carol — the last page, so Next is now disabled and Previous enabled.
        Assert.That(names(), Is.EqualTo(secondPage));
        Assert.That(page.FindAll("button")[1].HasAttribute("disabled"), Is.True, "Next disabled on last page");
        Assert.That(page.FindAll("button")[0].HasAttribute("disabled"), Is.False, "Previous enabled on page 2");
    }
}
