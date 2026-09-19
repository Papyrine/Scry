namespace Sample.WebClient.Pages.Live;

/// <summary>
/// Which transport the live pages are asking over, and the generated entry point for it. Nothing a
/// page writes differs between the two — the same LINQ, the same terminals — which is the whole of
/// what the switch is there to show.
/// </summary>
/// <remarks>
/// Over HTTP each live query is a request held open, which HTTP/2 shares one connection between and
/// HTTP/1.1 caps at six per origin. Over the hub every one of them shares the one socket.
/// </remarks>
public sealed class LiveTransport :
    IAsyncDisposable
{
    ScryQuery http;
    NavigationManager navigation;
    ScrySidecarStore sidecar;
    HubConnection? connection;
    ScryQuery? hub;

    public LiveTransport(ScryQuery http, ScryClient client, NavigationManager navigation, ScrySidecarStore sidecar)
    {
        this.http = http;
        this.navigation = navigation;
        this.sidecar = sidecar;

        // What the client knows about its own live queries, which the wire does not say: whether a
        // gap between two connections is really being retried. See /docs/sidecar.md.
        sidecar.Observe(client);
    }

    public bool SignalR { get; private set; }

    /// <summary>The entry point over whichever transport is selected.</summary>
    public ScryQuery Query => SignalR ? hub! : http;

    /// <summary>Raised after the transport changed, for a page to ask again over the new one.</summary>
    public event Func<Task>? Changed;

    public async Task Use(bool signalR)
    {
        if (signalR &&
            connection is null)
        {
            await Connect();
        }

        SignalR = signalR;
        if (Changed is not { } changed)
        {
            return;
        }

        foreach (var listener in changed.GetInvocationList().Cast<Func<Task>>())
        {
            await listener();
        }
    }

    // The connection is the app's to build, start and dispose. A client made over it is an ordinary
    // ScryClient, so the generated entry point wraps it like any other.
    async Task Connect()
    {
        // begin-snippet: signalRTransport
        connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri("/api/query-hub"))
            .WithAutomaticReconnect()
            .Build();
        await connection.StartAsync();
        var client = ScrySignalRClient.Create(connection);
        hub = new(client);
        // end-snippet

        // A hub carries its live queries on one socket that never reaches the sidecar's HTTP
        // handler, so this client is handed to the panel directly or they would not be listed.
        sidecar.Observe(client);
    }

    public ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            return connection.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }
}
