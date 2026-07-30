namespace Sample.Model;

public sealed class SampleContext(DbContextOptions<SampleContext> options) :
    DbContext(options)
{
    // begin-snippet: dbContext
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<EmployeeSummary> EmployeeSummaries => Set<EmployeeSummary>();
    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmployeeSummary>()
            .HasNoKey()
            .ToView("EmployeeSummary");

        // Table-per-hierarchy: the derived types share the base table and are told apart by a
        // discriminator, which is what OfType narrows on.
        modelBuilder.Entity<Vehicle>();
        modelBuilder.Entity<Building>();
    }
    // end-snippet

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

    public static void Initialize(SampleContext context)
    {
        context.Database.EnsureCreated();

        if (!context.Employees.Any())
        {
            Seed(context);
        }

        // EnsureCreated does not manage views; create it once the tables and data exist.
        context.Database.ExecuteSqlRaw(
            """
            CREATE OR ALTER VIEW EmployeeSummary AS
            SELECT d.Name AS Department, COUNT(e.Id) AS Headcount
            FROM Departments d
            LEFT JOIN Employees e ON e.DepartmentId = d.Id
            GROUP BY d.Name;
            """);
    }

    static void Seed(SampleContext context)
    {
        var engineering = new Department
        {
            Name = "Engineering"
        };
        var sales = new Department
        {
            Name = "Sales"
        };
        context.Departments.AddRange(engineering, sales);

        var alice = new Employee
        {
            Name = "Alice",
            Status = Status.FullTime,
            Active = true,
            Created = new(2026, 1, 4),
            Department = engineering,
            Salary = 200_000
        };
        context.Employees.Add(alice);
        context.Employees.AddRange(
            new()
            {
                Name = "Aaron",
                Status = Status.FullTime,
                Active = true,
                Created = new(2026, 1, 4),
                Department = engineering,
                Manager = alice,
                Salary = 150_000
            },
            new()
            {
                Name = "Bob",
                Status = Status.PartTime,
                Active = false,
                Created = new(2026, 2, 1),
                Department = sales,
                Manager = alice,
                Salary = 90_000
            },
            new()
            {
                Name = "Carol",
                Status = Status.Contractor,
                Active = true,
                Created = new(2026, 3, 1),
                Department = sales,
                Salary = 120_000
            });

        context.Assets.AddRange(
            new Vehicle
            {
                Name = "Van",
                Wheels = 4
            },
            new Vehicle
            {
                Name = "Trailer",
                Wheels = 2
            },
            new Building
            {
                Name = "Depot",
                Floors = 3
            });

        context.Orders.AddRange(
            new()
            {
                Region = "North",
                Amount = 100m
            },
            new()
            {
                Region = "North",
                Amount = 250m
            },
            new()
            {
                Region = "South",
                Amount = 75m
            });

        context.SaveChanges();
    }
}
