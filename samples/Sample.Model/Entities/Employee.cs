namespace Sample.Model;

// begin-snippet: queryableEntity
[Queryable]
public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Status Status { get; set; }
    public bool Active { get; set; }
    public DateOnly Created { get; set; }

    public int? ManagerId { get; set; }
    public Employee? Manager { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    // A claim check rather than a value: no query reads it, and what a client gets back is a handle
    // carrying this row's key. A photo is the case the attribute exists for — bytes nothing wants on
    // every row of every query, fetched by the one thing that actually wants to draw them. The check
    // that authorizes the fetch is registered by the server; this project references the annotations
    // alone, so [AttachmentWith] has no policy type to name here.
    [Attachment(ContentType = "image/svg+xml")]
    public byte[]? Photo { get; set; }

    // Never exposed to clients.
    [QueryIgnore]
    public decimal Salary { get; set; }

    // The other half of that pair: queryable, but never in a URL and never in a cache. [QueryIgnore]
    // hides a member outright; [Sensitive] keeps it askable while refusing the two ways its value
    // escapes — a query comparing it against a constant travels as a body rather than a URL, where the
    // constant would land in every access log on the way, and a response projecting it is sent
    // no-store, where a cacheable one would be written to the caller's disk.
    [Sensitive]
    public string Password { get; set; } = "";
}
// end-snippet