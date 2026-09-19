namespace Sample.WebClient.Pages.Live;

public partial class LiveStream
{
    IReadOnlyList<OrderRow>? orders;
    int answers;
    string? error;
    CancellationTokenSource? leaving;
    Task reading = Task.CompletedTask;

    protected override void Start()
    {
        leaving = new();
        reading = Read(leaving.Token);
    }

    // begin-snippet: liveStream
    async Task Read(CancellationToken leaving)
    {
        try
        {
            var live = Query
                .Order
                .OrderBy(_ => _.Id)
                .Select(_ => new OrderRow(_.Id, _.Region, _.Amount))
                .Live();

            // Runs for as long as the page is open. Cancelling is how it is told to stop, and what
            // tells the server the subscription is over.
            await foreach (var answer in live.WithCancellation(leaving))
            {
                orders = answer;
                answers++;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left.
        }
        catch (Exception exception)
        {
            error = exception.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected override async ValueTask Stop()
    {
        if (leaving is null)
        {
            return;
        }

        await leaving.CancelAsync();
        await reading;
        leaving.Dispose();
        leaving = null;
    }
    // end-snippet
}
