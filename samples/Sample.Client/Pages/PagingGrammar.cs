// ReSharper disable UnusedVariable
namespace Sample.Client.Pages;

// Documentation-only example backing the "grammar" section of docs/paging.md. Not wired to a page;
// it exists so that doc snippet compiles against the generated Query surface like any other sample.
public static class PagingGrammar
{
    public static async Task TwoPages(ScryQuery Query)
    {
        // begin-snippet: pagingGrammar
        // Page 1 — an ordered query with a page size.
        var page = await Query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Created)
            .ThenBy(_ => _.Id)
            .ToPageAsync(20);

        foreach (var row in page.Items)
        {
             /* ... */
        }

        // Page 2 — the same query, resumed with the previous page's cursor (a keyset seek).
        if (page.HasMore)
        {
            var next = await Query.Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Created)
                .ThenBy(_ => _.Id)
                .ToPageAsync(20, page.Cursor);
        }
        // end-snippet
    }
}
