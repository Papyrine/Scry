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

    public byte[] Avatar { get; set; } = [];

    [QueryIgnore]
    public decimal Salary { get; set; }
}

[Queryable]
public class Order
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public decimal Amount { get; set; }

    // Unsigned members: EF maps uint -> bigint and ulong -> decimal(20,0) on SQL Server. Neither has a
    // dedicated ClrTypeTag; their literals ride the String tag and are reconciled server-side.
    public uint Quantity { get; set; }
    public ulong Sku { get; set; }
}

/// <summary>
/// Carries its row policy via the [ReturnableWith] attribute rather than a programmatic AddPolicy,
/// exercising the attribute-discovery branch of Schema.ResolvePolicy.
/// </summary>
[Queryable]
[ReturnableWith(typeof(OpenTicketsOnlyPolicy))]
public class Ticket
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsOpen { get; set; }
}

// begin-snippet: namedSource
/// <summary>
/// Exposed to clients as 'Region', so the CLR type can be renamed without changing the wire
/// contract. Has no DbSet — nothing queries it; it exists to pin the naming behaviour.
/// </summary>
[Queryable(Name = "Region")]
public class SalesRegion
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
// end-snippet

/// <summary>
/// A keyless view opted in with [QueryableView]; introspection must report it with Kind 'View'. Has
/// no DbSet — nothing queries it; it exists to pin the view-classification behaviour.
/// </summary>
[QueryableView]
public class DepartmentHeadcount
{
    public string Department { get; set; } = "";
    public int Headcount { get; set; }
}

/// <summary>
/// Opted in with [Queryable] but marked EF [Keyless], which the schema treats as a view — the
/// documented equivalent of [QueryableView]. Pins that classification path.
/// </summary>
[Queryable]
[Keyless]
public class RegionSummary
{
    public string Region { get; set; } = "";
    public decimal Total { get; set; }
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

/// <summary>The [ReturnableWith] policy on <see cref="Ticket"/>: scopes queries to open tickets.</summary>
public sealed class OpenTicketsOnlyPolicy :
    IReturnablePolicy<Ticket>
{
    public IQueryable<Ticket> Filter(IQueryable<Ticket> source, ScryPolicyContext context) =>
        source.Where(_ => _.IsOpen);
}

/// <summary>
/// The inverse policy, registered via AddPolicy to prove it overrides <see cref="Ticket"/>'s
/// [ReturnableWith] attribute.
/// </summary>
public sealed class ClosedTicketsOnlyPolicy :
    IReturnablePolicy<Ticket>
{
    public IQueryable<Ticket> Filter(IQueryable<Ticket> source, ScryPolicyContext context) =>
        source.Where(_ => !_.IsOpen);
}

public sealed class TestContext(DbContextOptions<TestContext> options) :
    DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

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

        var alice = new Employee { Name = "Alice", Status = Status.FullTime, Active = true, Department = engineering, Salary = 200_000, Avatar = [0x01, 0x02, 0x03] };
        context.Employees.Add(alice);
        context.Employees.AddRange(
            new() { Name = "Aaron", Status = Status.FullTime, Active = true, Department = engineering, Manager = alice, Salary = 150_000, Avatar = [0x0A, 0x0B] },
            new() { Name = "Bob", Status = Status.PartTime, Active = false, Department = sales, Manager = alice, Salary = 90_000, Avatar = [0xFF] },
            new() { Name = "Carol", Status = Status.Contractor, Active = true, Department = sales, Salary = 120_000, Avatar = [] });

        context.Orders.AddRange(
            new() { Region = "North", Amount = 100m, Quantity = 3, Sku = 1000 },
            // Sku is deliberately above long.MaxValue to prove the value survives the String-tag path
            // (a numeric Int64 tag would overflow).
            new() { Region = "North", Amount = 250m, Quantity = 7, Sku = ulong.MaxValue },
            new() { Region = "South", Amount = 75m, Quantity = 1, Sku = 3000 });

        context.Tickets.AddRange(
            new() { Name = "Login bug", IsOpen = true },
            new() { Name = "Signup crash", IsOpen = true },
            new() { Name = "Old typo", IsOpen = false });

        context.SaveChanges();
    }
}
