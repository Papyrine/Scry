namespace Sample.WebClient.Pages.Live;

/// <summary>
/// What the live pages have in common, all of it to do with the transport switch: each starts its
/// live query over the selected transport, and stops and starts it again when the selection changes.
/// An app with one transport needs none of this — it starts in <c>OnInitialized</c>, stops in
/// <c>DisposeAsync</c>, and injects <c>ScryQuery</c> directly.
/// </summary>
public abstract class LivePage :
    ComponentBase,
    IAsyncDisposable
{
    [Inject]
    public LiveTransport Transport { get; set; } = null!;

    /// <summary>The generated entry point, over whichever transport is selected.</summary>
    protected ScryQuery Query => Transport.Query;

    /// <summary>Starts the page's live query. Nothing is asked of the server before this.</summary>
    protected abstract void Start();

    /// <summary>Ends it, which is what tells the server the subscription is over.</summary>
    protected abstract ValueTask Stop();

    protected override void OnInitialized()
    {
        Transport.Changed += Restart;
        Start();
    }

    async Task Restart()
    {
        await Stop();
        Start();
        await InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        Transport.Changed -= Restart;
        await Stop();
        GC.SuppressFinalize(this);
    }
}
