using System.Net.Http;
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
    // begin-snippet: wpfProjectionType
    record EmployeeRow(string Name, Status Status, string? Manager, string Department);
    // end-snippet

    // Ordinary LINQ, captured rather than executed: it is translated to the wire AST, validated
    // against the allow-list on the server, rebound to the real Employee, and run through EF Core.
    // begin-snippet: wpfQuery
    async Task<List<EmployeeRow>> ActiveEmployees() =>
        await query
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
}
