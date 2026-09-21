namespace Sample.WebClient.Pages.Live;

public partial class LiveCallback
{
    IReadOnlyList<OrderRow>? orders;
    int? count;
    string? error;
    ScrySubscription? rows;
    ScrySubscription? counting;

    // begin-snippet: liveCallback
    protected override void Start()
    {
        // The LINQ is what it would be for ToListAsync. Live() in its place means the answer keeps
        // arriving: now, and again whenever the rows it reads change.
        rows = Query
            .Order
            .OrderBy(_ => _.Id)
            .Select(_ => new OrderRow(_.Id, _.Region, _.Amount))
            .Live()
            .Subscribe(
                answer =>
                {
                    orders = answer;
                    InvokeAsync(StateHasChanged);
                },
                Failed);

        // Any terminal can be live. This one is a second subscription of its own, and the server
        // sends it a number rather than the rows.
        counting = Query
            .Order
            .LiveCount()
            .Subscribe(
                answer =>
                {
                    count = answer;
                    InvokeAsync(StateHasChanged);
                },
                Failed);
    }

    // A subscription outlives nothing: leaving the page ends it, and the server is told.
    protected override async ValueTask Stop()
    {
        if (rows is not null)
        {
            await rows.DisposeAsync();
        }

        if (counting is not null)
        {
            await counting.DisposeAsync();
        }
    }
    // end-snippet

    void Failed(Exception exception)
    {
        error = exception.Message;
        InvokeAsync(StateHasChanged);
    }
}
