using System.ComponentModel.DataAnnotations;

namespace Sample.Model;

// begin-snippet: commandMessages
/// <summary>Deletes one employee — an inactive one, by the sample's policy.</summary>
[Command(typeof(Employee))]
public class DeleteEmployee
{
    public int Id { get; set; }
}

/// <summary>Renames one employee. A name containing "slow" takes a while, to show a command going pending.</summary>
[Command(typeof(Employee))]
public class RenameEmployee
{
    public int Id { get; set; }

    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";
}

/// <summary>Deactivates or reactivates one employee: what makes a row deletable, and deletable again.</summary>
[Command(typeof(Employee))]
public class SetEmployeeActive
{
    public int Id { get; set; }
    public bool Active { get; set; }
}

/// <summary>Hires an employee, answering with the new row's id.</summary>
[Command(Result = typeof(EmployeeCreated))]
public class CreateEmployee
{
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";

    public int DepartmentId { get; set; }
    public Status Status { get; set; }
}

public class EmployeeCreated
{
    public int Id { get; set; }
}
// end-snippet
