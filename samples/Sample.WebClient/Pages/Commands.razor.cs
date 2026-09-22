namespace Sample.WebClient.Pages;

/// <summary>
/// Commands against a live table. The page sends; it never re-reads. Each command's outcome goes in the
/// status line, and what it wrote comes back through the live query, for this page and every other.
/// </summary>
public partial class Commands :
    IAsyncDisposable
{
    IReadOnlyList<EmployeeRow> rows = [];
    bool answered;
    bool capable;
    string? status;
    string? error;
    string hireName = "";
    string renameTo = "Renamed";
    ScrySubscription? employees;

    [Inject]
    ScryQuery Query { get; set; } = null!;

    [Inject]
    ScryClient Client { get; set; } = null!;

    // Set once the table and the capabilities are both in: what a test waits on before it clicks.
    string? Ready
    {
        get
        {
            if (answered && capable)
            {
                return "true";
            }

            return null;
        }
    }

    // begin-snippet: commandsPageQuery
    protected override async Task OnInitializedAsync()
    {
        // The Delete button of each row is its CanDeleteEmployee: the command's policy, decided per
        // row in the database, and decided again on every answer — so deactivating a row enables its
        // Delete as the next answer arrives.
        employees = Query
            .Employee
            .OrderBy(_ => _.Id)
            .Select(_ => new EmployeeRow(_.Id, _.Name, _.Active, _.CanDeleteEmployee))
            .Live()
            .Subscribe(
                answer =>
                {
                    rows = answer;
                    answered = true;
                    InvokeAsync(StateHasChanged);
                },
                exception =>
                {
                    error = exception.Message;
                    InvokeAsync(StateHasChanged);
                });

        // What this caller may send at all, for the buttons bound to Query.Commands.Can*. False until
        // the server has said, and said again whenever a denial shows it has moved.
        Client.CapabilitiesChanged += Redraw;
        await Client.Ready;
        capable = true;
    }
    // end-snippet

    void Redraw() =>
        InvokeAsync(StateHasChanged);

    // begin-snippet: commandsPageSend
    Task Delete(EmployeeRow row) =>
        Send(() => Query.Commands.DeleteEmployee(new() {Id = row.Id}), $"Deleted {row.Name}.");

    Task Rename(EmployeeRow row) =>
        Send(() => Query.Commands.RenameEmployee(new() {Id = row.Id, Name = renameTo}), $"Renamed {row.Name} to {renameTo}.");

    Task SetActive(EmployeeRow row)
    {
        var done = row.Active ? $"Deactivated {row.Name}." : $"Reactivated {row.Name}.";
        return Send(() => Query.Commands.SetEmployeeActive(new() {Id = row.Id, Active = !row.Active}), done);
    }

    // The typed outcome: the new row's id, read off the result the handler answered with.
    async Task Create()
    {
        var name = hireName;
        await Send(
            async () =>
            {
                var hired = await Query.Commands.CreateEmployee(new() {Name = name, DepartmentId = 1, Status = Status.FullTime});
                if (hired.Status == ScryCommandStatus.Completed)
                {
                    status = $"Hired {name} as #{hired.Value.Id}.";
                }

                return hired;
            },
            done: null);
    }

    async Task Send(Func<Task<ScryCommandOutcome>> send, string? done)
    {
        status = null;
        try
        {
            var outcome = await send();
            status = outcome.Status switch
            {
                ScryCommandStatus.Completed => done ?? status,

                // Still running when the client stopped waiting: the pending-work panel follows it to
                // its end, and the table shows what it wrote when it lands.
                ScryCommandStatus.Pending => $"{outcome.Command} is taking a while. It is in pending work.",
                _ => outcome.Error
            };
        }
        catch (Exception exception)
        {
            // Refused before it ran — malformed, denied, one too many — so nothing was written.
            status = exception.Message;
        }
    }
    // end-snippet

    public async ValueTask DisposeAsync()
    {
        Client.CapabilitiesChanged -= Redraw;
        if (employees is not null)
        {
            await employees.DisposeAsync();
        }
    }
}

/// <summary>One row of the commands page: an employee, and whether this caller may delete them.</summary>
public sealed record EmployeeRow(int Id, string Name, bool Active, bool CanDeleteEmployee);
