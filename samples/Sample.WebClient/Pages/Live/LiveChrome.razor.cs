namespace Sample.WebClient.Pages.Live;

/// <summary>
/// What the live pages share: the tabs between them, and the two writes that give them something to
/// show. Neither write tells the page anything — each page hears about it the way any client would,
/// from the server, because the rows its query reads changed.
/// </summary>
/// <remarks>
/// The reprice is a command, sent over whichever transport the switch selects: over the hub, the
/// command and every live query share the one connection.
/// </remarks>
public partial class LiveChrome
{
    string? error;

    /// <summary>Which tab this is, so it can be marked as the current one.</summary>
    [Parameter]
    [EditorRequired]
    public string Active { get; set; } = "";

    string Class(string tab) =>
        tab == Active ? "active" : "";

    string TransportName
    {
        get
        {
            if (Transport.SignalR)
            {
                return "signalr";
            }

            return "sse";
        }
    }

    /// <summary>
    /// The <c>RepriceOrder</c> command, which the server's handler saves through its context, and its
    /// change interceptor reports.
    /// </summary>
    async Task Reprice()
    {
        error = null;
        try
        {
            var outcome = await Transport.Query.Commands.RepriceOrder(new() {Id = 1});
            if (outcome.Status is ScryCommandStatus.Failed or ScryCommandStatus.Unknown)
            {
                error = outcome.Error;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    /// <summary>
    /// A bulk update, which no interceptor can see. The server says what it wrote instead.
    /// </summary>
    Task RepriceBulk() =>
        Post("/api/orders/reprice-bulk");

    /// <summary>
    /// Asks over the other transport. The page stops its live query and starts it again over the one
    /// selected, and nothing else about it changes — which is what there is to see.
    /// </summary>
    async Task Use(bool signalR)
    {
        error = null;
        try
        {
            await Transport.Use(signalR);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    async Task Post(string url)
    {
        error = null;
        try
        {
            var response = await Clients.CreateClient("api").PostAsync(url, content: null);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }
}
