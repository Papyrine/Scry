namespace Sample.Client.Pages;

public partial class Index
{
    // begin-snippet: clientProjectionTypes
    record EmployeeRow(string Name, Status Status, string? Manager, string Department);

    record RegionSummary(string Region, decimal Total, int Count);
    // end-snippet

    // Captured as locals so the LINQ below closes over them — the query is parameterized at runtime
    // rather than hard-coded, exactly how an app would build a filtered query.
    readonly Status status = Status.FullTime;
    readonly int top = 2;

    List<EmployeeRow>? employees;
    List<RegionSummary>? regions;
    List<EmployeeRow>? fullTimers;
    string? error;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // begin-snippet: clientQuery
            employees = await Query.Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
                .ToListAsync();
            // end-snippet

            // begin-snippet: clientGroupBy
            regions = await Query.Order
                .GroupBy(_ => _.Region)
                .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
                .ToListAsync();
            // end-snippet

            // begin-snippet: clientClosureCapture
            fullTimers = await Query.Employee
                .Where(_ => _.Status == status)
                .OrderBy(_ => _.Name)
                .Take(top)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
                .ToListAsync();
            // end-snippet
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }
}
