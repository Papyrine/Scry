namespace Sample.WebClient.Pages.Live;

public partial class LiveMessagePipe
{
    string? error;
    ScrySubscription? subscription;

    // begin-snippet: liveMessagePipe
    // The whole of the integration: a live query's callback is a publisher's Publish. Buffered, so a
    // component that subscribes after an answer arrived is handed that answer rather than nothing.
    protected override void Start() =>
        subscription = Query
            .Order
            .OrderBy(_ => _.Id)
            .Select(_ => new OrderRow(_.Id, _.Region, _.Amount))
            .Live()
            .Subscribe(
                answer => Publisher.Publish(new(answer)),
                exception =>
                {
                    error = exception.Message;
                    InvokeAsync(StateHasChanged);
                });
    // end-snippet

    protected override ValueTask Stop()
    {
        if (subscription is not null)
        {
            return subscription.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }
}
