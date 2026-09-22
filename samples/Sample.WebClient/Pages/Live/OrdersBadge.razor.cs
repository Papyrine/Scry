namespace Sample.WebClient.Pages.Live;

public partial class OrdersBadge :
    IDisposable
{
    int count;
    decimal total;
    IDisposable? subscription;

    protected override void OnInitialized() =>
        subscription = Subscriber.Subscribe(
            changed =>
            {
                count = changed.Orders.Count;
                total = changed.Orders.Sum(_ => _.Amount);
                InvokeAsync(StateHasChanged);
            });

    public void Dispose() =>
        subscription?.Dispose();
}
