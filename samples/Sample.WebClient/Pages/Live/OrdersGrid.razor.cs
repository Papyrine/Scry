namespace Sample.WebClient.Pages.Live;

public partial class OrdersGrid :
    IDisposable
{
    IReadOnlyList<OrderRow>? orders;
    IDisposable? subscription;

    protected override void OnInitialized() =>
        subscription = Subscriber.Subscribe(
            changed =>
            {
                orders = changed.Orders;
                InvokeAsync(StateHasChanged);
            });

    public void Dispose() =>
        subscription?.Dispose();
}
