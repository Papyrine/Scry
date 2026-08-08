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

    // Aggregable, not projectable: a client can ask how many employees a department has, or whether
    // any matches a predicate, but can never enumerate them into a result.
    [QueryableCollection]
    public List<Employee> Employees { get; set; } = [];
}