namespace Sample.WpfClient;

public partial class MainWindow
{
    readonly ScryQuery query;
    readonly RenameEmployeeCommand rename;

    public MainWindow(ScryQuery query, ScryClient client)
    {
        this.query = query;
        rename = new(query, () => EmployeeGrid.SelectedItem as EmployeeRow, () => RenameText.Text, _ => StatusText.Text = _ ?? "");
        InitializeComponent();
        DataContext = this;

        // Asked again when what the server says this caller may send moves, and when the selection does.
        client.CapabilitiesChanged += rename.Raise;
        EmployeeGrid.SelectionChanged += (_, _) => rename.Raise();
        Loaded += async (_, _) => await LoadEmployees();
    }

    /// <summary>What the Rename button is bound to.</summary>
    public ICommand Rename => rename;

    // The shape the window wants, declared here rather than anywhere the server knows about. The
    // response comes back keyed by these names. The key is projected like any other member, because a
    // command names its row by it; the grid just does not show it.
    // ReSharper disable NotAccessedPositionalProperty.Local
    // begin-snippet: wpfProjectionType
    record EmployeeRow(int Id, string Name, Status Status, string? Manager, string Department);
    // end-snippet
    // ReSharper restore NotAccessedPositionalProperty.Local

    void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs args) =>
        args.Cancel = args.PropertyName == nameof(EmployeeRow.Id);

    // Ordinary LINQ, captured rather than executed: it is translated to the wire AST, validated
    // against the allow-list on the server, rebound to the real Employee, and run through EF Core.
    // begin-snippet: wpfQuery
    Task<List<EmployeeRow>> ActiveEmployees() =>
        query
            .Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Id, _.Name, _.Status, _.Manager!.Name, _.Department!.Name))
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
            .Select(_ => new EmployeeRow(_.Id, _.Name, _.Status, _.Manager!.Name, _.Department!.Name))
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

    // begin-snippet: wpfCommand
    // The XAML shape of a capability: an ICommand whose CanExecute is what the server last said this
    // caller may send, and whether there is a row to send it against. WPF re-reads it whenever
    // CanExecuteChanged is raised, which the window does as either of those moves.
    sealed class RenameEmployeeCommand(
        ScryQuery query,
        Func<EmployeeRow?> selected,
        Func<string> name,
        Action<string?> report) :
        ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            query.Commands.CanRenameEmployee &&
            selected() is not null;

        public async void Execute(object? parameter)
        {
            if (selected() is not { } row)
            {
                return;
            }

            try
            {
                var outcome = await query.Commands.RenameEmployee(new() {Id = row.Id, Name = name()});
                report(
                    outcome.Status switch
                    {
                        ScryCommandStatus.Completed => $"Renamed {row.Name}.",
                        ScryCommandStatus.Pending => "Still renaming.",
                        _ => outcome.Error
                    });
            }
            catch (Exception exception)
            {
                report(exception.Message);
            }
        }

        public void Raise() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
    // end-snippet
}
