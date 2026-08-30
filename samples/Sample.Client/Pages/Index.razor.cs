namespace Sample.Client.Pages;

public partial class Index
{
    // begin-snippet: clientProjectionTypes
    record EmployeeRow(string Name, Status Status, string? Manager, string Department);

    record RegionSummary(string Region, decimal Total, int Count);
    // end-snippet

    // begin-snippet: clientAttachmentType
    // The photo is not a value this row carries: the query brings back a handle, and Id has to be
    // projected beside it because that is the key the bytes are fetched by.
    record EmployeePhoto(int Id, string Name, ScryAttachment Photo);
    // end-snippet

    // begin-snippet: clientNestedProjectionTypes
    // A nested result shape: the Department navigation projects into its own object rather than being
    // flattened into a column.
    record EmployeeCard(string Name, DepartmentCard Department);

    record DepartmentCard(string Name);
    // end-snippet

    // Captured as locals so the LINQ below closes over them — the query is parameterized at runtime
    // rather than hard-coded, exactly how an app would build a filtered query.
    readonly Status status = Status.FullTime;
    readonly int top = 2;

    List<EmployeeRow>? employees;
    List<RegionSummary>? regions;
    List<EmployeeRow>? fullTimers;
    List<EmployeeCard>? cards;
    List<EmployeePhoto>? photos;

    // The fetched bytes, as data URIs an <img> can be pointed at, keyed by the row they were claimed
    // by. Absent for a row whose photo is null, which is why the flag below exists: "not fetched yet"
    // and "there is nothing to fetch" render differently.
    readonly Dictionary<int, string> faces = [];
    bool facesFetched;
    string? error;
    bool stale;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // begin-snippet: clientQuery
            employees = await Query
                .Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
                .ToListAsync();
            // end-snippet

            // begin-snippet: clientGroupBy
            regions = await Query
                .Order
                .GroupBy(_ => _.Region)
                .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
                .ToListAsync();
            // end-snippet

            // begin-snippet: clientClosureCapture
            fullTimers = await Query
                .Employee
                .Where(_ => _.Status == status)
                .OrderBy(_ => _.Name)
                .Take(top)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
                .ToListAsync();
            // end-snippet

            // ReSharper disable once ArrangeObjectCreationWhenTypeNotEvident

            // begin-snippet: clientNestedProjection
            // Projecting into the Department navigation builds a nested result object rather than
            // flattening it — the response is { Name, Department: { Name } }.
            cards = await Query
                .Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new EmployeeCard(_.Name, new DepartmentCard(_.Department!.Name)))
                .ToListAsync();
            // end-snippet

            // begin-snippet: clientAttachmentQuery
            // No bytes travel with this. Every row comes back holding a handle to its photo and the
            // key that handle is redeemed by; the response is the same size whether the photos are
            // eight bytes or eight megabytes.
            photos = await Query
                .Employee
                .OrderBy(_ => _.Name)
                .Select(_ => new EmployeePhoto(_.Id, _.Name, _.Photo))
                .ToListAsync();
            // end-snippet

            // The names are on screen before a single image is asked for, which is the point being
            // made: the page decides what it wants to draw, and only then pays for it.
            StateHasChanged();

            // begin-snippet: clientAttachmentFetch
            // One request per face, each authorized on its own terms by the server's IAttachmentPolicy.
            foreach (var photo in photos)
            {
                if (await FaceAsync(photo.Photo) is { } face)
                {
                    faces[photo.Id] = face;
                }
            }
            // end-snippet

            facesFetched = true;
        }
        // begin-snippet: handleStaleClient
        // The query failed because this deployed app was generated against a model surface the server
        // no longer has. SchemaStaleDetected has already fired on the same response, so the reload
        // banner is showing; render a directed placeholder for the data that could not load, rather
        // than presenting the failure as an application error.
        catch (ScryStaleClientException)
        {
            stale = true;
        }
        // end-snippet
        catch (Exception exception)
        {
            error = exception.Message;
        }
    }

    // begin-snippet: clientAttachmentOpen
    /// <summary>
    /// Redeems one handle for its bytes, or null when the row holds no photo — a readable row with an
    /// empty column, which the server answers with a 204 rather than by refusing. The caller owns the
    /// stream and disposes it; a real photo would stream rather than land in memory whole, which this
    /// one does only because it ends up in an <c>img</c> tag. The media type below is the one
    /// <c>Employee.Photo</c> declares, and the one the fetch was served as.
    /// </summary>
    static async Task<string?> FaceAsync(ScryAttachment photo)
    {
        await using var bytes = await photo.OpenAsync();
        if (bytes is null)
        {
            return null;
        }

        var buffer = new MemoryStream();
        await bytes.CopyToAsync(buffer);
        return $"data:image/svg+xml;base64,{Convert.ToBase64String(buffer.ToArray())}";
    }
    // end-snippet
}
