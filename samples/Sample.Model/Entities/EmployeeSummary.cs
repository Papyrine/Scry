namespace Sample.Model;

// begin-snippet: queryableView
/// <summary>A keyless EF Core entity mapped to a database view.</summary>
[QueryableView]
public class EmployeeSummary
{
    public string Department { get; set; } = "";
    public int Headcount { get; set; }
}
// end-snippet