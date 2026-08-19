using Bunit;
using Microsoft.AspNetCore.Components;
using PermissionsPage = Sample.Client.Pages.Permissions;

/// <summary>
/// Renders the real /permissions page against the real Scry pipeline and drives its three buttons,
/// which is what makes the sample a demonstration rather than an assertion that a page loads: the
/// counter it shows is how often the expensive decision actually ran.
/// </summary>
/// <remarks>
/// A server of its own rather than the shared one. Revoking a region is server state every other
/// in-process fixture would then be reading, and one of them renders the same orders.
/// </remarks>
[TestFixture]
public class CachedPolicyPageTests
{
    ScryTestServer server = null!;

    [OneTimeSetUp]
    public async Task StartServer() =>
        server = await ScryTestServer.StartAsync();

    [OneTimeTearDown]
    public async Task StopServer() =>
        await server.DisposeAsync();

    [Test]
    public async Task DrivesTheCacheThroughItsThreeCases()
    {
        await using var context = new BunitContext();
        context.Services.AddSingleton(server.CreateScryClient());
        context.Services.AddSingleton<ScryQuery>();
        context.Services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(server.CreateClient()));

        var page = context.Render<PermissionsPage>();
        await page.WaitForStateAsync(
            () => page.FindAll("tbody tr").Count > 0,
            TimeSpan.FromSeconds(10));

        // Re-read the DOM each time so a re-render after a click is what is being asserted on.
        string[] regions() => [.. page.FindAll("tbody tr td:first-child").Select(_ => _.TextContent)];
        int decisions() => int.Parse(page.Find("#decisions").TextContent);

        // The seeded orders, all of them: the sample grants both regions until something revokes one.
        Assert.That(regions(), Is.EqualTo(["North", "North", "South"]));

        // Running the query again decides nothing. This is the whole point of the feature — an
        // ordinary policy would have re-run its filter over every row.
        var before = decisions();
        await page.Find("#reload").ClickAsync();
        await page.WaitForStateAsync(() => page.FindAll("tbody tr").Count == 3, TimeSpan.FromSeconds(10));

        Assert.That(decisions(), Is.EqualTo(before), "a repeat query decided a row again");
        Assert.That(regions(), Is.EqualTo(["North", "North", "South"]));

        // Revising one order moves its revision past the watermark this scope was decided up to, so
        // the next query decides that row and no other. The same path makes an inserted row correct
        // on its first read.
        await page.Find("#revise").ClickAsync();
        await page.WaitForStateAsync(() => decisions() > before, TimeSpan.FromSeconds(10));

        Assert.That(decisions(), Is.EqualTo(before + 1), "revising one order decided more than one row");
        Assert.That(regions(), Is.EqualTo(["North", "North", "South"]));

        // Revoking a region changes no order, so nothing but the host could know the answers are
        // stale. The rows go, which proves the invalidation reached the query.
        before = decisions();
        await page.Find("#grant-South").ChangeAsync(new ChangeEventArgs {Value = false});
        await page.WaitForStateAsync(() => page.FindAll("tbody tr").Count == 2, TimeSpan.FromSeconds(10));

        Assert.That(regions(), Is.EqualTo(["North", "North"]));
        Assert.That(decisions(), Is.EqualTo(before + 3), "the scope was not decided again from scratch");
    }

    /// <summary>Hands the page a client bound to the test server, in place of the browser's factory.</summary>
    sealed class SingleClientFactory(HttpClient client) :
        IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
