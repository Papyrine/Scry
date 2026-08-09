namespace Sample.Model;

[Queryable]
public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Sent as a raw multipart part rather than as base64 inside the JSON payload. Only the transfer
    // changes: the queryable surface is an ordinary byte[], and a client reads the same bytes whether
    // or not the server diverted them.
    [BinaryTransfer]
    public byte[]? Logo { get; set; }

    // The other trade, beside the one above: never read by a query at all. A client sees a handle
    // carrying this row's key and exchanges it for the bytes only when something wants them. The
    // check that authorizes the exchange is registered by the server — this project references the
    // annotations alone, so [AttachmentWith] has no policy type to name here.
    [Attachment]
    public byte[]? Handbook { get; set; }

    // Aggregable, not projectable: a client can ask how many employees a department has, or whether
    // any matches a predicate, but can never enumerate them into a result.
    [QueryableCollection]
    public List<Employee> Employees { get; set; } = [];
}
