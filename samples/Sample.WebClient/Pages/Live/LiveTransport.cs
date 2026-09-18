using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

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
public sealed class LiveTransport(ScryQuery http, NavigationManager navigation) :
    IAsyncDisposable
{
    HubConnection? connection;
    ScryQuery? hub;

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
    // begin-snippet: signalRTransport
    async Task Connect()
    {
        connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri("/api/query-hub"))
            .WithAutomaticReconnect()
            .Build();
        await connection.StartAsync();
        hub = new(ScrySignalRClient.Create(connection));
    }
    // end-snippet

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }
}
