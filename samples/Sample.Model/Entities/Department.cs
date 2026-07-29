namespace Sample.Model;

[Queryable]
public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Aggregable, not projectable: a client can ask how many employees a department has, or whether
    // any matches a predicate, but can never enumerate them into a result.
    [QueryableCollection]
    public List<Employee> Employees { get; set; } = [];
}