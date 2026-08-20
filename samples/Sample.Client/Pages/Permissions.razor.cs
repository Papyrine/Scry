using System.Net.Http.Json;

namespace Sample.Client.Pages;

public partial class Permissions
{
    record OrderRow(int Id, string Region, decimal Amount);

    record GrantState(IReadOnlyList<string> Regions, IReadOnlyList<string> Granted, int Lookups);

    List<OrderRow>? orders;
    GrantState? grants;
    int decisions;
    string? error;

    protected override Task OnInitializedAsync() =>
        Load();

    /// <summary>
    /// The query, and the sample's own view of what deciding it cost. Nothing here says the policy is
    /// cached — the LINQ is what it would be for any other policied source.
    /// </summary>
    async Task Load()
    {
        error = null;
        try
        {
            // Asked fresh rather than answered conditionally. Everywhere else in this sample a repeat
            // query is a 304 and that is the point, but this page exists to show what the server
            // decided — and a 304 is the server not deciding anything, so it would show nothing.
            // Delta names this the read-after-write escape; see /docs/caching.md.
            orders = await Query
                .Order
                .WithHeader("Cache-Control", "no-cache")
                .OrderBy(_ => _.Region)
                .ThenBy(_ => _.Amount)
                .Select(_ => new OrderRow(_.Id, _.Region, _.Amount))
                .ToListAsync();

            grants = await Clients.CreateClient("api").GetFromJsonAsync<GrantState>("/api/grants");
            decisions = grants?.Lookups ?? 0;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    Task Reload() =>
        Load();

    /// <summary>Moves one order's revision past the watermark, so the next query re-decides it.</summary>
    async Task Revise()
    {
        if (orders is not [var first, ..])
        {
            return;
        }

        await Post($"/api/orders/{first.Id}/touch");
        await Load();
    }

    /// <summary>
    /// Grants or revokes a region. The server changes its own authorization data and then tells Scry,
    /// which is what makes the change reach a query.
    /// </summary>
    async Task Set(string region, bool allowed)
    {
        await Post($"/api/grants/{region}?allowed={allowed}");
        await Load();
    }

    async Task Post(string url)
    {
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
