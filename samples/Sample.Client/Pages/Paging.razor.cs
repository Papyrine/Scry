using Scry.Wire;

namespace Sample.Client.Pages;

public partial class Paging
{
    record EmployeeRow(string Name, Status Status, string Department);

    // A small page size so the four seeded employees span two pages.
    const int pageSize = 2;

    int pageIndex;
    ScryPage<EmployeeRow>? page;
    string? error;

    protected override Task OnInitializedAsync() => Load();

    async Task Load()
    {
        try
        {
            // begin-snippet: clientPaging
            // Offset paging: Skip to the start of the page, then take one page of rows. The response is
            // a ScryPage — the rows plus HasMore, which the UI uses to enable or disable the Next button.
            page = await Query.Employee
                .OrderBy(_ => _.Name)
                .Skip(pageIndex * pageSize)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Department!.Name))
                .ToPageAsync(pageSize);
            // end-snippet
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    Task NextPage()
    {
        pageIndex++;
        return Load();
    }

    Task PreviousPage()
    {
        if (pageIndex == 0)
        {
            return Task.CompletedTask;
        }

        pageIndex--;
        return Load();
    }
}
