namespace Sample.CommandHandlers;

/// <summary>
/// Deletes an employee — inactive, since the policy has already said so of this row — unless they
/// manage anyone. That refusal is the sample's one failure a client is shown in the handler's words.
/// </summary>
public sealed class DeleteEmployeeHandler(SampleContext data) :
    ICommandHandler<DeleteEmployee>
{
    public async Task Handle(DeleteEmployee command, ScryCommandContext context, Cancel cancel)
    {
        if (await data.Employees.AnyAsync(_ => _.ManagerId == command.Id, cancel))
        {
            throw new ScryCommandException("This employee manages others. Reassign their reports first.");
        }

        var employee = await data.Employees.SingleAsync(_ => _.Id == command.Id, cancel);
        data.Employees.Remove(employee);
    }
}

// begin-snippet: slowCommandHandler
/// <summary>
/// Renames an employee. Saved by the pipeline once this returns, through the host's context — and so
/// through its change interceptor, which is what re-asks the live queries that read the name.
/// </summary>
public sealed class RenameEmployeeHandler(SampleContext data, IOptions<SampleCommandOptions> options) :
    ICommandHandler<RenameEmployee>
{
    public async Task Handle(RenameEmployee command, ScryCommandContext context, Cancel cancel)
    {
        // Stands in for work that takes a while: long enough past the sync window that the command is
        // answered as pending, and the client's pending-work panel shows it until it lands.
        if (command.Name.Contains("slow", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(options.Value.SlowDelay, cancel);
        }

        var employee = await data.Employees.SingleAsync(_ => _.Id == command.Id, cancel);
        employee.Name = command.Name;
    }
}
// end-snippet

public sealed class SetEmployeeActiveHandler(SampleContext data) :
    ICommandHandler<SetEmployeeActive>
{
    public async Task Handle(SetEmployeeActive command, ScryCommandContext context, Cancel cancel)
    {
        var employee = await data.Employees.SingleAsync(_ => _.Id == command.Id, cancel);
        employee.Active = command.Active;
    }
}

/// <summary>Hires an employee, saving to learn the new row's id, which is what it answers with.</summary>
public sealed class CreateEmployeeHandler(SampleContext data) :
    ICommandHandler<CreateEmployee, EmployeeCreated>
{
    public async Task<EmployeeCreated> Handle(CreateEmployee command, ScryCommandContext context, Cancel cancel)
    {
        if (!await data.Departments.AnyAsync(_ => _.Id == command.DepartmentId, cancel))
        {
            throw new ScryCommandException("There is no such department.");
        }

        var employee = new Employee
        {
            Name = command.Name,
            DepartmentId = command.DepartmentId,
            Status = command.Status,
            Active = true,
            Created = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        data.Employees.Add(employee);
        await data.SaveChangesAsync(cancel);
        return new()
        {
            Id = employee.Id
        };
    }
}

// begin-snippet: liveSavedWrite
/// <summary>
/// Reprices one order. Saved through the context, so the interceptor reports it: the live queries
/// reading Order are asked again as soon as this commits, and nothing here has to say so.
/// </summary>
public sealed class RepriceOrderHandler(SampleContext data) :
    ICommandHandler<RepriceOrder>
{
    public async Task Handle(RepriceOrder command, ScryCommandContext context, Cancel cancel)
    {
        var orders = data.Orders;
        var order = await orders.SingleAsync(_ => _.Id == command.Id, cancel);
        order.Amount += 1;
        order.Revision = await orders.MaxAsync(_ => _.Revision, cancel) + 1;
    }
}
// end-snippet
