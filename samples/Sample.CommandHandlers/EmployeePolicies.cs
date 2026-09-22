namespace Sample.CommandHandlers;

// begin-snippet: commandPolicy
/// <summary>
/// Only an inactive employee may be deleted. Checked on every delete sent — a row this refuses is
/// answered exactly as a row that is not there — and computed per row as
/// <c>Employee.CanDeleteEmployee</c>, in the database, for every query that reads it.
/// </summary>
public sealed class DeleteEmployeePolicy :
    ICommandPolicy<DeleteEmployee, Employee>
{
    public bool Allow(ScryPolicyContext context) => true;

    public Expression<Func<Employee, bool>> Rows(ScryPolicyContext context) =>
        _ => !_.Active;
}
// end-snippet

/// <summary>
/// Whether this caller may hire at all: read from the sample's options, where a real app reads the
/// caller's role off <c>context.Services</c>. What <c>Query.Commands.CanCreateEmployee</c> reports.
/// </summary>
public sealed class CreateEmployeePolicy(IOptions<SampleCommandOptions> options) :
    ICommandPolicy<CreateEmployee>
{
    public bool Allow(ScryPolicyContext context) =>
        options.Value.AllowCreate;
}
