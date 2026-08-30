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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<EmployeeSummary>()
            .HasNoKey()
            .ToView("EmployeeSummary");

        // Table-per-hierarchy: the derived types share the base table and are told apart by a
        // discriminator, which is what OfType narrows on.
        builder.Entity<Vehicle>();
        builder.Entity<Building>();
    }
    // end-snippet

    protected override void ConfigureConventions(ModelConfigurationBuilder builder) =>
        builder.Properties<decimal>().HavePrecision(18, 2);

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
            Name = "Engineering",
            // A PNG signature stands in for a real logo — enough bytes to travel as its own multipart
            // part. Sales keeps a null one, so a query over both carries a diverted value and an
            // inline null side by side.
            Logo = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            // Never travels with a row at all — fetched by this row's key, and only when asked for.
            // Sales keeps a null one here too: a row that exists holding no value is a distinct answer
            // from a row that may not be read.
            Handbook = "Engineering handbook."u8.ToArray()
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
            Salary = 200_000,
            // Never travels with a row at all — fetched by this row's key, and only when something
            // wants to draw it.
            Photo = CartoonFace.For("Alice")
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
                Salary = 150_000,
                Photo = CartoonFace.For("Aaron")
            },
            new()
            {
                Name = "Bob",
                Status = Status.PartTime,
                Active = false,
                Created = new(2026, 2, 1),
                Department = sales,
                Manager = alice,
                Salary = 90_000,
                Photo = CartoonFace.For("Bob")
            },
            new()
            {
                Name = "Carol",
                Status = Status.Contractor,
                Active = true,
                Created = new(2026, 3, 1),
                Department = sales,
                // Carol keeps a null one: a row that exists holding no value is a distinct answer
                // from a row that may not be read, and the sample page shows both.
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
                Amount = 100m,
                Revision = 1,
                Tags = ["urgent", "export"]
            },
            new()
            {
                Region = "North",
                Amount = 250m,
                Revision = 2,
                Tags = ["export"]
            },
            new()
            {
                Region = "South",
                Amount = 75m,
                Revision = 3
            });

        context.SaveChanges();
    }
}
