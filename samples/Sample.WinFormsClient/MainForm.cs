namespace Sample.WinFormsClient;

sealed class MainForm : Form
{
    readonly ScryQuery query;
    readonly BindingSource binding = new();
    readonly Button refresh;
    readonly Label status;

    public MainForm(ScryQuery query)
    {
        this.query = query;

        Text = "Scry - Windows Forms sample";
        Width = 760;
        Height = 480;

        refresh = new()
        {
            Text = "Refresh",
            Left = 12,
            Top = 12,
            Width = 100
        };
        refresh.Click += async (_, _) => await LoadEmployees();

        status = new()
        {
            Left = 124,
            Top = 16,
            Width = 600,
            AutoSize = false
        };

        DataGridView grid1 = new()
        {
            Left = 12,
            Top = 48,
            Width = 720,
            Height = 380,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            DataSource = binding
        };

        Controls.Add(refresh);
        Controls.Add(status);
        Controls.Add(grid1);

        Shown += async (_, _) => await LoadEmployees();
    }

    // The shape the form wants, declared here rather than anywhere the server knows about. The
    // response comes back keyed by these names.
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeRow(string Name, Status Status, string? Manager, string Department);
    // ReSharper restore NotAccessedPositionalProperty.Local

    // The same LINQ the WPF and console samples write. It is captured rather than executed, then
    // translated to the wire AST, validated against the allow-list on the server, and run there.
    async Task LoadEmployees()
    {
        refresh.Enabled = false;
        status.Text = "Loading...";
        try
        {
            var rows = await query
                .Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
                .ToListAsync();

            binding.DataSource = rows;
            status.Text = $"{rows.Count} active employees";
        }
        catch (HttpRequestException exception)
        {
            binding.DataSource = Array.Empty<EmployeeRow>();
            status.Text = $"Cannot reach {Program.ServerAddress}. Start the server with " +
                          $"'dotnet run --project samples/Sample.WebServer'. ({exception.Message})";
        }
        finally
        {
            refresh.Enabled = true;
        }
    }
}
