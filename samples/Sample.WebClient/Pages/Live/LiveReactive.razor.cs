using System.Globalization;

namespace Sample.WebClient.Pages.Live;

public partial class LiveReactive
{
    record Totals(int Orders, decimal Total, decimal Change);

    Totals? totals;
    string? error;
    IDisposable? subscription;

    // Signed, so that a rise and a fall read differently.
    static string Change(Totals totals) =>
        totals.Change.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture);

    // begin-snippet: liveReactive
    protected override void Start() =>
        subscription = Query
            .Order
            .Select(_ => new OrderRow(_.Id, _.Region, _.Amount))
            .Live()
            // From here down it is Rx. Each answer is the whole result, so an operator that wants
            // the difference between two of them folds them together itself.
            .AsObservable()
            .Select(_ =>
                new Totals(
                    _.Count,
                    _.Sum(_ => _.Amount),
                    Change: 0))
            .Scan((previous, next) => next with
            {
                Change = next.Total - previous.Total
            })
            .DistinctUntilChanged()
            .Subscribe(
                next =>
                {
                    totals = next;
                    InvokeAsync(StateHasChanged);
                },
                exception =>
                {
                    error = exception.Message;
                    InvokeAsync(StateHasChanged);
                });

    // Disposing the Rx subscription disposes the one under it, which ends the live query.
    protected override ValueTask Stop()
    {
        subscription?.Dispose();
        return ValueTask.CompletedTask;
    }
    // end-snippet
}
