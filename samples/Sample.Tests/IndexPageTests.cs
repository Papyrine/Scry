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

        await using var context = new BunitContext();
        context.Services.AddSingleton(server.CreateScryClient());
        context.Services.AddSingleton<ScryQuery>();

        var page = context.Render<IndexPage>();
        await page.WaitForStateAsync(
            () => page.FindAll("table").Count == 4,
            TimeSpan.FromSeconds(10));

        await Verify(page);
    }

    // The page's stale branch: a query failing with ScryStaleClientException (the server attributed
    // the failure to this client's schema stamp, or a result carried an enum value the generated
    // model does not have) renders the directed reload prompt instead of the generic error. The
    // custom transport stands in for the HTTP path, whose classification is covered by the
    // integration tests.
    [Test]
    public async Task RendersStalePromptWhenQueryFailsStale()
    {
        await using var context = new BunitContext();
        context.Services.AddSingleton(new ScryClient((_, _) =>
            Task.FromException<QueryResponse>(
                new ScryStaleClientException(
                    "Property 'Renamed' is not allow-listed on 'Employee'. The request's schema stamp does " +
                    "not match this server's model, so the client was generated against a different model " +
                    "surface — regenerate the client."))));
        context.Services.AddSingleton<ScryQuery>();

        var page = context.Render<IndexPage>();
        await page.WaitForStateAsync(
            () => page.FindAll("p.stale").Count == 1,
            TimeSpan.FromSeconds(10));

        await Verify(page);
    }

    [Test]
    public async Task RendersErrorWhenServerRejectsQuery()
    {
        var server = await SharedScryServer.InstanceAsync();

        await using var context = new BunitContext();
        // Point the client at an endpoint that does not exist so the query fails and the page
        // takes its error branch. The HttpClient CreateClient() returns is a fresh in-memory
        // TestServer client — no socket or handler resources — and the server owns its own lifetime,
        // so it is intentionally left undisposed here (ScryClient does not own it, and the container
        // won't dispose an AddSingleton instance either).
        context.Services.AddSingleton(ScryClient.ForHttp(server.CreateClient(), "/api/missing"));
        context.Services.AddSingleton<ScryQuery>();

        var page = context.Render<IndexPage>();
        await page.WaitForStateAsync(
            () => page.FindAll("p.error").Count == 1,
            TimeSpan.FromSeconds(10));

        await Verify(page);
    }
}
