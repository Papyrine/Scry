using System.Net.Http;
using System.Reactive.Linq;
using System.Windows;

namespace Sample.WpfClient;

public partial class MainWindow
{
    readonly ScryQuery query;

    public MainWindow(ScryQuery query)
    {
        this.query = query;
        InitializeComponent();
        Loaded += async (_, _) => await LoadEmployees();
    }

    // The shape the window wants, declared here rather than anywhere the server knows about. The
    // response comes back keyed by these names.
    // ReSharper disable NotAccessedPositionalProperty.Local
    // begin-snippet: wpfProjectionType
    record EmployeeRow(string Name, Status Status, string? Manager, string Department);
    // end-snippet
    // ReSharper restore NotAccessedPositionalProperty.Local

    // Ordinary LINQ, captured rather than executed: it is translated to the wire AST, validated
    // against the allow-list on the server, rebound to the real Employee, and run through EF Core.
    // begin-snippet: wpfQuery
    Task<List<EmployeeRow>> ActiveEmployees() =>
        query
            .Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
            .ToListAsync();
    // end-snippet

    async Task LoadEmployees()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Loading...";
        try
        {
            var rows = await ActiveEmployees();
            EmployeeGrid.ItemsSource = rows;
            StatusText.Text = $"{rows.Count} active employees";
        }
        catch (HttpRequestException exception)
        {
            EmployeeGrid.ItemsSource = null;
            StatusText.Text = $"Cannot reach {App.ServerAddress}. Start the server with " +
                              $"'dotnet run --project samples/Sample.WebServer'. ({exception.Message})";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    async void OnRefreshClick(object sender, RoutedEventArgs args) =>
        await LoadEmployees();

    IDisposable? live;

    // The same query, kept answered. AsObservable hands over a plain IObservable and captures no
    // context, because saying where to be called is what an Rx pipeline does for itself: ObserveOn
    // brings each answer to the dispatcher, where the grid may be touched.
    // begin-snippet: wpfLive
    void OnLiveChanged(object sender, RoutedEventArgs args)
    {
        live?.Dispose();
        live = null;
        RefreshButton.IsEnabled = LiveCheckBox.IsChecked != true;
        if (LiveCheckBox.IsChecked != true)
        {
            return;
        }

        StatusText.Text = "Connecting...";
        live = query
            .Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
            .Live()
            .AsObservable()
            .ObserveOn(SynchronizationContext.Current!)
            .Subscribe(
                rows =>
                {
                    EmployeeGrid.ItemsSource = rows;
                    StatusText.Text = $"{rows.Count} active employees, live as of {DateTime.Now:T}";
                },
                exception => StatusText.Text = $"The live query ended: {exception.Message}");
    }
    // end-snippet

    protected override void OnClosed(EventArgs args)
    {
        live?.Dispose();
        base.OnClosed(args);
    }
}
