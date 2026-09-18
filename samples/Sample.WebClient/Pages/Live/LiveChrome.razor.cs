using Microsoft.AspNetCore.Components;

namespace Sample.WebClient.Pages.Live;

/// <summary>
/// What the live pages share: the tabs between them, and the two writes that give them something to
/// show. Neither write tells the page anything — each page hears about it the way any client would,
/// from the server, because the rows its query reads changed.
/// </summary>
public partial class LiveChrome
{
    string? error;

    /// <summary>Which tab this is, so it can be marked as the current one.</summary>
    [Parameter]
    [EditorRequired]
    public string Active { get; set; } = "";

    string Class(string tab) =>
        tab == Active ? "active" : "";

    /// <summary>Saved through the server's context, which its change interceptor reports.</summary>
    Task Reprice() =>
        Post("/api/orders/1/reprice");

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
