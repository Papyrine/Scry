using Microsoft.EntityFrameworkCore;

namespace Scry.Tests;

public enum Status
{
    FullTime,
    PartTime,
    Contractor
}

[Queryable]
public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Employee> Employees { get; set; } = [];
}

[Queryable]
public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Status Status { get; set; }
    public bool Active { get; set; }

    public int? ManagerId { get; set; }
    public Employee? Manager { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    [QueryIgnore]
    public decimal Salary { get; set; }
}

[Queryable]
public class Order
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public decimal Amount { get; set; }
}

[QueryablePoco]
public class Holiday
{
    public string Name { get; set; } = "";
    public Date Date { get; set; }

    public static IEnumerable<Holiday> Seed() =>
    [
        new() { Name = "New Year", Date = new(2026, 1, 1) },
        new() { Name = "Workers Day", Date = new(2026, 5, 1) },
        new() { Name = "Christmas", Date = new(2026, 12, 25) }
    ];
}

// begin-snippet: returnablePolicy
/// <summary>A row policy that scopes <see cref="Employee"/> queries to active rows only.</summary>
public sealed class ActiveOnlyPolicy :
    IReturnablePolicy<Employee>
{
    public IQueryable<Employee> Filter(IQueryable<Employee> source, ScryPolicyContext context) =>
        source.Where(_ => _.Active);
}
// end-snippet

public sealed class TestContext(DbContextOptions<TestContext> options) :
    DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

    static SqlInstance<TestContext> sqlInstance = null!;
    static SqlDatabase<TestContext> database = null!;

    // Every test in this assembly is read-only against the same seed, so the whole fixture shares a
    // single LocalDB database built once by DatabaseSetup. CreateSeeded hands out a fresh context over
    // that database, which keeps it synchronous and leaves the call sites unchanged.
    public static async Task InitializeAsync()
    {
        sqlInstance = new(_ => new(_.Options));
        database = await sqlInstance.Build();
        Seed(database.Context);
    }

    public static async Task ShutdownAsync()
    {
        await database.DisposeAsync();
        sqlInstance.Dispose();
    }

    public static TestContext CreateSeeded() =>
        database.NewConnectionOwnedDbContext();

    static void Seed(TestContext context)
    {
        var engineering = new Department { Name = "Engineering" };
        var sales = new Department { Name = "Sales" };
        context.Departments.AddRange(engineering, sales);

        var alice = new Employee { Name = "Alice", Status = Status.FullTime, Active = true, Department = engineering, Salary = 200_000 };
        context.Employees.Add(alice);
        context.Employees.AddRange(
            new() { Name = "Aaron", Status = Status.FullTime, Active = true, Department = engineering, Manager = alice, Salary = 150_000 },
            new() { Name = "Bob", Status = Status.PartTime, Active = false, Department = sales, Manager = alice, Salary = 90_000 },
            new() { Name = "Carol", Status = Status.Contractor, Active = true, Department = sales, Salary = 120_000 });

        context.Orders.AddRange(
            new() { Region = "North", Amount = 100m },
            new() { Region = "North", Amount = 250m },
            new() { Region = "South", Amount = 75m });

        context.SaveChanges();
    }
}
