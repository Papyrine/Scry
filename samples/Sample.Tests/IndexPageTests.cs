using Bunit;
using IndexPage = Sample.Client.Pages.Index;

// Renders the real Index page against the real Scry server pipeline (in-memory), then snapshots
// the produced markup. This exercises the page, its controls, and the client/server round trip.
[TestFixture]
public class IndexPageTests
{
    [Test]
    public async Task RendersEmployeeAndRegionTables()
    {
        var server = await SharedScryServer.InstanceAsync();

        using var context = new BunitContext();
        context.Services.AddSingleton(server.CreateScryClient());
        context.Services.AddSingleton<ScryQuery>();

        var page = context.Render<IndexPage>();
        await page.WaitForStateAsync(() => page.FindAll("table").Count == 3, TimeSpan.FromSeconds(10));

        await Verify(page);
    }

    [Test]
    public async Task RendersErrorWhenServerRejectsQuery()
    {
        var server = await SharedScryServer.InstanceAsync();

        using var context = new BunitContext();
        // Point the client at an endpoint that does not exist so the query fails and the page
        // takes its error branch.
        context.Services.AddSingleton(ScryClient.ForHttp(server.CreateClient(), "/api/missing"));
        context.Services.AddSingleton<ScryQuery>();

        var page = context.Render<IndexPage>();
        await page.WaitForStateAsync(() => page.FindAll("p.error").Count == 1, TimeSpan.FromSeconds(10));

        await Verify(page);
    }
}
