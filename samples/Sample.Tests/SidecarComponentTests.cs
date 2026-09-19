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

    // Three kinds of exchange are shown without a body, each for a reason of its own, and the panel
    // says which — an empty pane would read as a response that was empty.
    [TestCase("/api/query/subscribe", "text/event-stream", "a live query's answers arrive for as long as it is open")]
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
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(options);
        context.Services.AddSingleton(store);
        var component = context.Render<ScrySidecar>();
        await component.WaitForStateAsync(
            () => component.FindAll("[data-testid=sidecar-toggle]").Count == 1,
            TimeSpan.FromSeconds(10));
        await component.Find("[data-testid=sidecar-toggle]").ClickAsync(new());
        await component.Find("[data-testid=sidecar-entries] li").ClickAsync(new());

        var detail = component.Find("[data-testid=sidecar-detail]");
        Assert.Multiple(() =>
        {
            Assert.That(detail.TextContent, Does.Contain($"Body not captured — {why}"));
            Assert.That(component.FindAll("[data-testid=sidecar-response]"), Is.Empty);
        });
    }

    sealed class Served(string contentType) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var content = new ByteArrayContent([1, 2, 3]);
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
