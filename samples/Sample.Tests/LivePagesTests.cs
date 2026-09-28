using Bunit;
using Sample.WebClient.Pages.Live;

/// <summary>
/// Renders the real /live pages against the real Scry pipeline and presses their buttons. Each page
/// consumes the same live query a different way, and each has to do the same thing: show the rows,
/// and show them again, changed, without being asked to — because the server said they had.
/// </summary>
/// <remarks>
/// A server of its own, since these write. The pages never refetch: nothing in them runs after a
/// button is pressed except the callback a new answer arrives through, which is what is being pinned.
/// </remarks>
[NotInParallel]
public class LivePagesTests
{
    static ScryTestServer server = null!;

    [Before(Class)]
    public static async Task StartServer() =>
        server = await ScryTestServer.StartAsync(liveQueries: true, commands: true);

    [After(Class)]
    public static async Task StopServer() =>
        await server.DisposeAsync();

    [Test]
    public async Task ACallbackIsHandedTheRowsAndThenTheChange()
    {
        await using var context = Context();
        var page = context.Render<LiveCallback>();
        await page.WaitForStateAsync(() => page.FindAll("#orders tbody tr").Count == 3, patience);
        var before = Amounts(page);

        // Saved through the server's context, which its interceptor reports.
        await page.Find("#reprice").ClickAsync();
        await page.WaitForStateAsync(() => Amounts(page)[0] == before[0] + 1, patience);

        using (Assert.Multiple())
        {
            await Assert.That(Amounts(page)[1..]).IsEquivalentTo(before[1..], CollectionOrdering.Matching).Because("Only the first order was repriced.");
            await Assert.That(page.Find("#count").TextContent).IsEqualTo("3 live");
        }
    }

    // A bulk update never passes through SaveChanges, so no interceptor sees it. The server says what
    // it wrote instead, and the page hears of it exactly as it hears of a save.
    [Test]
    public async Task AWriteOnlyTheHostCouldReportStillArrives()
    {
        await using var context = Context();
        var page = context.Render<LiveCallback>();
        await page.WaitForStateAsync(() => page.FindAll("#orders tbody tr").Count == 3, patience);
        var before = Amounts(page);

        await page.Find("#reprice-bulk").ClickAsync();
        await page.WaitForStateAsync(() => Amounts(page)[2] == before[2] + 1, patience);

        await Assert.That(Amounts(page)).IsEquivalentTo(before.Select(_ => _ + 1), CollectionOrdering.Matching);
    }

    [Test]
    public async Task AStreamIsReadAnAnswerAtATime()
    {
        await using var context = Context();
        var page = context.Render<LiveStream>();
        await page.WaitForStateAsync(() => page.FindAll("#orders tbody tr").Count == 3, patience);
        var before = Amounts(page);

        await page.Find("#reprice").ClickAsync();
        await page.WaitForStateAsync(() => Amounts(page)[0] == before[0] + 1, patience);

        await Assert.That(int.Parse(page.Find("#answers").TextContent)).IsGreaterThanOrEqualTo(2);
    }

    // Rx's own operators over the interface Scry hands it: the change is folded out of two answers.
    [Test]
    public async Task RxFoldsTheChangeOutOfTwoAnswers()
    {
        await using var context = Context();
        var page = context.Render<LiveReactive>();
        await page.WaitForStateAsync(() => page.FindAll("#total").Count == 1, patience);
        var before = decimal.Parse(page.Find("#total").TextContent);

        await page.Find("#reprice").ClickAsync();
        await page.WaitForStateAsync(() => decimal.Parse(page.Find("#total").TextContent) == before + 1, patience);

        using (Assert.Multiple())
        {
            await Assert.That(page.Find("#change").TextContent).IsEqualTo("+1.00");
            await Assert.That(page.Find("#orders").TextContent).IsEqualTo("3");
        }
    }

    // One server subscription, and two components that know nothing about Scry both fed by it.
    [Test]
    public async Task TwoComponentsShareOneLiveQueryThroughTheBus()
    {
        await using var context = Context();
        var page = context.Render<LiveMessagePipe>();
        await page.WaitForStateAsync(() => page.FindAll("#orders tbody tr").Count == 3, patience);
        var before = Amounts(page);

        await page.Find("#reprice").ClickAsync();
        await page.WaitForStateAsync(() => Amounts(page)[0] == before[0] + 1, patience);

        using (Assert.Multiple())
        {
            await Assert.That(page.Find("#count").TextContent).IsEqualTo("3");
            await Assert.That(decimal.Parse(page.Find("#total").TextContent)).IsEqualTo(before.Sum() + 1);
        }
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static decimal[] Amounts<TPage>(IRenderedComponent<TPage> page)
        where TPage : Microsoft.AspNetCore.Components.IComponent =>
    [
        .. page
            .FindAll("#orders tbody tr td:last-child")
            .Select(_ => decimal.Parse(_.TextContent))
    ];

    static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddSingleton(server.CreateScryClient());
        context.Services.AddSingleton<ScryQuery>();
        context.Services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(server.CreateClient()));
        context.Services.AddMessagePipe(_ => _.EnableAutoRegistration = false);

        // The app registers these, and the transport hands the sidecar its clients so that live
        // queries carried on a hub are listed at all — see /docs/sidecar.md.
        context.Services.AddScrySidecar();

        // Left on HTTP here: the switch needs a socket, which the browser suite has and this does not.
        context.Services.AddScoped<LiveTransport>();
        return context;
    }

    /// <summary>Hands the page a client bound to the test server, in place of the browser's factory.</summary>
    sealed class SingleClientFactory(HttpClient client) :
        IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
