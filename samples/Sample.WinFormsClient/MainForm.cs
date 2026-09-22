namespace Sample.WinFormsClient;

sealed class MainForm : Form
{
    readonly ScryQuery query;
    readonly ScryPendingWorkStore work;
    readonly BindingSource binding = new();
    readonly Button refresh;
    readonly CheckBox live;
    readonly Label status;
    readonly TextBox renameTo;
    readonly DataGridView grid;
    readonly ListBox pending;
    ScrySubscription? subscription;

    public MainForm(ScryQuery query, ScryPendingWorkStore work)
    {
        this.query = query;
        this.work = work;

        Text = "Scry - Windows Forms sample";
        Width = 760;
        Height = 560;

        refresh = new()
        {
            Text = "Refresh",
            Left = 12,
            Top = 12,
            Width = 100
        };
        refresh.Click += async (_, _) => await LoadEmployees();

        live = new()
        {
            Text = "Live",
            Left = 124,
            Top = 15,
            Width = 60
        };
        live.CheckedChanged += (_, _) => OnLiveChanged();

        status = new()
        {
            Left = 190,
            Top = 16,
            Width = 540,
            AutoSize = false
        };

        renameTo = new()
        {
            Left = 12,
            Top = 48,
            Width = 200,
            Text = "Renamed"
        };

        Button rename = new()
        {
            Text = "Rename selected",
            Left = 220,
            Top = 46,
            Width = 130
        };
        rename.Click += async (_, _) => await RenameSelected();

        grid = new()
        {
            Left = 12,
            Top = 80,
            Width = 720,
            Height = 330,
            ReadOnly = true,
            AllowUserToAddRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            DataSource = binding
        };

        // The key travels with every row, because a command names its row by key; it just is not
        // anything a person needs to read.
        grid.DataBindingComplete += (_, _) =>
        {
            if (grid.Columns["Id"] is { } id)
            {
                id.Visible = false;
            }
        };

        pending = new()
        {
            Left = 12,
            Top = 418,
            Width = 720,
            Height = 90,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        work.Changed += ShowPendingWork;

        Controls.Add(refresh);
        Controls.Add(live);
        Controls.Add(status);
        Controls.Add(renameTo);
        Controls.Add(rename);
        Controls.Add(grid);
        Controls.Add(pending);

        Shown += async (_, _) => await LoadEmployees();
    }

    // The shape the form wants, declared here rather than anywhere the server knows about. The
    // response comes back keyed by these names.
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeRow(int Id, string Name, Status Status, string? Manager, string Department);
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
                .Select(_ => new EmployeeRow(_.Id, _.Name, _.Status, _.Manager!.Name, _.Department!.Name))
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

    // The same query, kept answered. Subscribe is called on the UI thread, so that is where each
    // answer is delivered: the callback touches the binding with no Invoke, while the reading and
    // deserializing happen off the thread that paints.
    // begin-snippet: winFormsLive
    void OnLiveChanged()
    {
        subscription?.Dispose();
        subscription = null;
        refresh.Enabled = !live.Checked;
        if (!live.Checked)
        {
            return;
        }

        status.Text = "Connecting...";
        subscription = query
            .Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Id, _.Name, _.Status, _.Manager!.Name, _.Department!.Name))
            .Live()
            .Subscribe(
                rows =>
                {
                    binding.DataSource = rows;
                    status.Text = $"{rows.Count} active employees, live as of {DateTime.Now:T}";
                },
                exception => status.Text = $"The live query ended: {exception.Message}");
    }
    // end-snippet

    // A command acts on a row by its key, which the grid carries for exactly this. The grid shows the
    // new name when the live query's next answer arrives, or on the next Refresh; nothing here edits it.
    // begin-snippet: winFormsCommand
    async Task RenameSelected()
    {
        if (grid.CurrentRow?.DataBoundItem is not EmployeeRow row)
        {
            status.Text = "Select an employee to rename.";
            return;
        }

        try
        {
            var outcome = await query.Commands.RenameEmployee(new() {Id = row.Id, Name = renameTo.Text});
            status.Text = outcome.Status switch
            {
                ScryCommandStatus.Completed => $"Renamed {row.Name} to {renameTo.Text}.",
                ScryCommandStatus.Pending => "Still renaming. It is listed under pending work.",
                _ => outcome.Error
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or ScryRequestException or ScryPermissionException)
        {
            status.Text = exception.Message;
        }
    }

    // The store says it changed on the context the command was sent from — the UI thread, since
    // RenameSelected runs there — so the list is redrawn with no Invoke.
    void ShowPendingWork() =>
        pending.DataSource = work.Items.Select(Describe).ToList();

    static string Describe(ScryPendingCommand item)
    {
        var line = $"{item.Command} {string.Join(", ", item.Keys)}: {item.Status}, {item.Elapsed.TotalSeconds:0.0}s";
        if (item.Error is { } error)
        {
            return $"{line} — {error}";
        }

        return line;
    }
    // end-snippet

    protected override void OnFormClosed(FormClosedEventArgs args)
    {
        work.Changed -= ShowPendingWork;
        subscription?.Dispose();
        base.OnFormClosed(args);
    }
}
