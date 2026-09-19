using Bunit;

/// <summary>
/// The sidecar component's contextual toggle button: shown by default, absent under
/// <see cref="ScrySidecarOptions.Never"/>, and decidable by a predicate over the app's services —
/// which is how an app keys it off the current user.
/// </summary>
[TestFixture]
public class SidecarComponentTests
{
    [Test]
    public async Task ToggleButtonShowsByDefault()
    {
        await using var context = Context(new());

        var component = context.Render<ScrySidecar>();
        await component.WaitForStateAsync(
            () => component.FindAll("[data-testid=sidecar-toggle]").Count == 1,
            TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task NeverHidesTheToggleButton()
    {
        await using var context = Context(new()
        {
            ToggleButton = ScrySidecarOptions.Never
        });

        var component = context.Render<ScrySidecar>();

        // The predicate runs after the first render; give it that pass before asserting absence.
        await Task.Delay(50);
        component.Render();
        Assert.That(component.FindAll("[data-testid=sidecar-toggle]"), Is.Empty);
    }

    // The predicate receives the app's services, so a decision from the current context — here a
    // stand-in for reading the signed-in user — reaches the button.
    [Test]
    public async Task PredicateDecidesFromTheCurrentContext()
    {
        var options = new ScrySidecarOptions
        {
            ToggleButton = async services =>
            {
                var user = services.GetRequiredService<FakeCurrentUser>();
                await Task.Yield();
                return user.IsDeveloper;
            }
        };
        await using var context = Context(options);
        context.Services.AddSingleton(new FakeCurrentUser(IsDeveloper: true));

        var component = context.Render<ScrySidecar>();
        await component.WaitForStateAsync(
            () => component.FindAll("[data-testid=sidecar-toggle]").Count == 1,
            TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task PredicateHidesFromTheCurrentContext()
    {
        var options = new ScrySidecarOptions
        {
            ToggleButton = services =>
                ValueTask.FromResult(services.GetRequiredService<FakeCurrentUser>().IsDeveloper)
        };
        await using var context = Context(options);
        context.Services.AddSingleton(new FakeCurrentUser(IsDeveloper: false));

        var component = context.Render<ScrySidecar>();

        await Task.Delay(50);
        component.Render();
        Assert.That(component.FindAll("[data-testid=sidecar-toggle]"), Is.Empty);
    }

    // Two kinds of exchange are shown without a body, each for a reason of its own, and the panel
    // says which — an empty pane would read as a response that was empty.
    [TestCase("/api/query/stream", "application/x-ndjson", "streams are read row by row")]
    [TestCase("/api/query/attachment", "application/octet-stream", "attachment bytes are never cached")]
    public async Task AnExchangeWhoseBodyIsNeverReadSaysWhy(string path, string contentType, string why)
    {
        var options = new ScrySidecarOptions();
        var store = new ScrySidecarStore(options);
        using var client = new HttpClient(
            new ScrySidecarHandler(store, options)
            {
                InnerHandler = new Served(contentType)
            })
        {
            BaseAddress = new("http://localhost")
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                path.EndsWith("attachment")
                    ? """{"version":1,"root":"Employee","member":"Photo","keys":[]}"""
                    : ScryJson.Serialize(QueryRequest.Create("Employee", [])),
                Encoding.UTF8,
                "application/json")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await using var context = new BunitContext();
        var component = await Panel(context, options, store);
        await component.Find("[data-testid=sidecar-entries] .scry-sidecar-row").ClickAsync(new());

        var detail = component.Find("[data-testid=sidecar-detail]");
        Assert.Multiple(() =>
        {
            Assert.That(detail.TextContent, Does.Contain($"Body not captured — {why}"));
            Assert.That(component.FindAll("[data-testid=sidecar-response]"), Is.Empty);
        });
    }

    // A live query is a session rather than an exchange, so its row shows what state it is in and
    // how much has arrived, in place of a status and a latency that stopped being true immediately.
    [Test]
    public async Task ALiveQueryRendersAsASession()
    {
        var (options, store) = await Live();

        await using var context = new BunitContext();
        var component = await Panel(context, options, store);
        var row = component.Find("[data-testid=sidecar-session]");

        Assert.Multiple(() =>
        {
            Assert.That(row.TextContent, Does.Contain("Order"));
            Assert.That(component.Find("[data-testid=sidecar-session-state]").TextContent, Is.EqualTo("closed"));

            // One answer, and how long since anything last arrived.
            Assert.That(component.Find("[data-testid=sidecar-session-counts]").TextContent, Does.StartWith("1 ·"));
        });
    }

    // The pane the panel could never fill before: a live query's answer.
    [Test]
    public async Task ALiveQueryShowsItsLatestAnswer()
    {
        var (options, store) = await Live();

        await using var context = new BunitContext();
        var component = await Panel(context, options, store);
        await component.Find("[data-testid=sidecar-session]").ClickAsync(new());

        Assert.Multiple(() =>
        {
            Assert.That(component.Find("[data-testid=sidecar-session-summary]").TextContent, Does.Contain("1 answer"));
            Assert.That(component.Find("[data-testid=sidecar-latest-answer]").TextContent, Does.Contain("\"kind\""));
        });
    }

    // The connections are under the row they held open, and each one's events under it.
    [Test]
    public async Task ALiveQueryExpandsToItsConnectionsAndTheirEvents()
    {
        var (options, store) = await Live();
        await using var context = new BunitContext();
        var component = await Panel(context, options, store);

        Assert.That(component.FindAll("[data-testid=sidecar-connection]"), Is.Empty);
        await component.Find("[data-testid=sidecar-expand]").ClickAsync(new());

        var connections = component.FindAll("[data-testid=sidecar-connection]");
        Assert.That(connections, Has.Count.EqualTo(1));
        await connections[0].ClickAsync(new());

        var events = component.Find("[data-testid=sidecar-events]").TextContent;
        Assert.Multiple(() =>
        {
            Assert.That(events, Does.Contain(ScryLive.Result));
            Assert.That(events, Does.Contain(ScryLive.Ping));
            Assert.That(events, Does.Contain("a3f1"));
            Assert.That(component.Find("[data-testid=sidecar-connection-summary]").TextContent, Does.Contain("lifetime"));
        });

        // An answer that was kept opens onto itself; a heartbeat has nothing to open onto.
        Assert.That(component.FindAll("[data-testid=sidecar-event-data]"), Is.Empty);
        await component.FindAll("[data-testid=sidecar-events] tr")[0].ClickAsync(new());
        Assert.That(component.Find("[data-testid=sidecar-event-data]").TextContent, Does.Contain("\"kind\""));

        await component.FindAll("[data-testid=sidecar-events] tr")[1].ClickAsync(new());
        Assert.That(component.FindAll("[data-testid=sidecar-event-data]"), Is.Empty);
    }

    // One answer, a heartbeat, and the server saying it had reached the connection's lifetime.
    static async Task<(ScrySidecarOptions Options, ScrySidecarStore Store)> Live()
    {
        var options = new ScrySidecarOptions();
        var store = new ScrySidecarStore(options);
        var answer = ScryJson.Serialize(
            QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(Array.Empty<int>())));
        var end = Encoding.UTF8.GetString(ScryJson.SerializeToUtf8(new ScryLiveEnd(false) {Reason = "lifetime"}));
        var body = $"event: result\nid: a3f1\ndata: {answer}\n\nevent: ping\ndata: \n\nevent: end\ndata: {end}\n\n";

        using var client = new HttpClient(
            new ScrySidecarHandler(store, options)
            {
                InnerHandler = new Served(ScryLive.ContentType, Encoding.UTF8.GetBytes(body))
            })
        {
            BaseAddress = new("http://localhost")
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/query/subscribe")
        {
            Content = new StringContent(
                ScryJson.Serialize(QueryRequest.Create("Order", [])),
                Encoding.UTF8,
                "application/json")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // Nothing is read ahead of the consumer, so the events are only framed once they are pulled.
        await response.Content.ReadAsByteArrayAsync();
        return (options, store);
    }

    static async Task<IRenderedComponent<ScrySidecar>> Panel(
        BunitContext context,
        ScrySidecarOptions options,
        ScrySidecarStore store)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(options);
        context.Services.AddSingleton(store);
        var component = context.Render<ScrySidecar>();
        await component.WaitForStateAsync(
            () => component.FindAll("[data-testid=sidecar-toggle]").Count == 1,
            TimeSpan.FromSeconds(10));
        await component.Find("[data-testid=sidecar-toggle]").ClickAsync(new());
        return component;
    }

    sealed class Served(string contentType, byte[]? body = null) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var content = new StreamContent(new MemoryStream(body ?? [1, 2, 3]));
            content.Headers.ContentType = new(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = content});
        }
    }

    record FakeCurrentUser(bool IsDeveloper);

    static BunitContext Context(ScrySidecarOptions options)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(options);
        context.Services.AddSingleton(new ScrySidecarStore(options));
        return context;
    }
}
