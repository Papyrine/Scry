
namespace Sample.Client.Pages;

public partial class KeysetPaging
{
    record EmployeeRow(string Name, Status Status, string Department);

    const int pageSize = 2;

    ScryPage<EmployeeRow>? page;
    // The current page's cursor — Next resumes from it.
    string? cursor;
    string? error;

    protected override Task OnInitializedAsync() => Load(from: null);

    async Task Load(string? from)
    {
        try
        {
            // begin-snippet: clientCursorPaging
            // Keyset paging: pass the previous page's opaque Cursor to resume exactly past its last row.
            // The query must be ordered; the server seeks (WHERE Name > … ORDER BY Name, Id) instead of
            // counting an offset, so it stays fast and stable as rows are added or removed.
            page = await Query.Employee
                .OrderBy(_ => _.Name)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Department!.Name))
                .ToPageAsync(pageSize, from);
            // end-snippet
            cursor = page.Cursor;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    Task Next() =>
        Load(cursor);

    Task StartOver()
    {
        cursor = null;
        return Load(from: null);
    }
}
