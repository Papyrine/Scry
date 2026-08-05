using Microsoft.EntityFrameworkCore;

// begin-snippet: previousNamesEnumValue
public enum Status
{
    FullTime,
    PartTime,

    // Renamed from 'Freelancer'; enum value names are sent on the wire as constants, so clients
    // generated before the rename keep resolving.
    [PreviousNames("Freelancer")]
    Contractor
}
// end-snippet

[Queryable]
public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Deliberately not opted in: Employee carries a row policy in some tests, and a collection of a
    // policied type is refused at startup.
    public List<Employee> Employees { get; set; } = [];
}

[Flags]
public enum Perks
{
    None = 0,
    Parking = 1,
    Gym = 2,
    Remote = 4
}

[Queryable]
public class Employee
{
    public int Id { get; set; }

    // begin-snippet: previousNamesMember
    // Renamed from 'FullName'; the previous name still resolves for clients generated before it.
    [PreviousNames("FullName")]
    public string Name { get; set; } = "";
    // end-snippet
    public Status Status { get; set; }

    // A [Flags] member, which is what HasFlag reads. A combined constant travels by name — "Parking,
    // Gym" — exactly as Enum.ToString spells it, and Enum.Parse reads it back.
    public Perks Perks { get; set; }
    public bool Active { get; set; }

    public int? ManagerId { get; set; }
    public Employee? Manager { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    public byte[] Avatar { get; set; } = [];

    // A complex type mapped to a JSON column (see TestContext.OnModelCreating). Traversable via
    // [QueryableComplex]; exercises Scry rebinding member access that EF translates into the JSON column.
    public Address Address { get; set; } = new();

    // begin-snippet: queryableComplexCollection
    // A JSON array of value objects: a complex-type collection mapped into one column. Aggregable and
    // flattenable exactly like a collection of entities — the element type being [QueryableComplex]
    // rather than a source changes nothing about how a client queries it.
    [QueryableCollection]
    public List<Address> PreviousAddresses { get; set; } = [];
    // end-snippet

    [QueryIgnore]
    public decimal Salary { get; set; }
}

/// <summary>
/// A complex value type mapped to JSON. Opted in with [QueryableComplex]: reachable only by
/// traversing from <see cref="Employee"/> (e.g. Address.City), never as a root source. Zip is hidden.
/// </summary>
// begin-snippet: queryableComplex
[QueryableComplex]
public class Address
{
    public string City { get; set; } = "";
    public string Country { get; set; } = "";

    [QueryIgnore]
    public string Zip { get; set; } = "";
}
// end-snippet

/// <summary>
/// The root of a TPH hierarchy. Opting the base in exposes its own members; a derived type is only
/// reachable — and its own members only readable — once it is opted in on its own.
/// </summary>
// begin-snippet: queryableHierarchy
[Queryable]
public class Asset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

[Queryable]
public class Vehicle : Asset
{
    public int Wheels { get; set; }
}

[Queryable]
public class Building : Asset
{
    public int Floors { get; set; }
}
// end-snippet

/// <summary>
/// Derives from <see cref="Asset"/> but is deliberately <i>not</i> opted in, so narrowing to it is
/// rejected and its own members stay unreachable.
/// </summary>
public class Artwork : Asset
{
    public string Medium { get; set; } = "";
}

/// <summary>
/// Exposed only through <see cref="Order.Priorities"/>, a collection of values — nothing else names
/// it. The enum still has to reach the client, since its value names are sent on the wire as constants.
/// </summary>
public enum Priority
{
    Low,
    High
}

[Queryable]
public class Order
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public decimal Amount { get; set; }

    // Unsigned members: EF maps uint -> bigint and ulong -> decimal(20,0) on SQL Server. Neither has a
    // dedicated ClrTypeTag; their literals use the String tag and are reconciled server-side.
    public uint Quantity { get; set; }
    public ulong Sku { get; set; }

    // A real DateTime column, so the date functions are exercised as translated SQL rather than in
    // memory. Discount is optional, which is what the coalesce and nullable-aggregate paths need.
    public DateTime Placed { get; set; }
    public decimal? Discount { get; set; }

    // A char member: primitive, so already a scalar on both sides. Present to pin that a char constant
    // survives the wire, where it uses the String tag.
    public char Grade { get; set; }

    // Numeric text, which is what the parsing functions read. Chosen so that numeric order and string
    // order disagree — "8" sorts after "40" as text and before it as a number.
    public string Code { get; set; } = "";

    // Boolean text, which is what BooleanFrom reads.
    public string Audited { get; set; } = "";

    // begin-snippet: queryableCollection
    // Opted in for aggregation: a client can ask how many lines an order has, or what they total, but
    // can never enumerate them into a result.
    [QueryableCollection]
    public List<OrderLine> Lines { get; set; } = [];
    // end-snippet

    // begin-snippet: queryablePrimitiveCollection
    // EF primitive collections — collections of values, which the provider stores as a JSON column.
    // They opt in like any other collection; what differs is that their elements are values, so a
    // question about them reads the element itself rather than a member of it.
    [QueryableCollection]
    public List<string> Tags { get; set; } = [];

    [QueryableCollection]
    public List<int> Scores { get; set; } = [];
    // end-snippet

    // Priority is reachable from nothing else, so this pins that an enum a collection of values reaches
    // is re-emitted to clients exactly as one a scalar member reaches.
    [QueryableCollection]
    public List<Priority> Priorities { get; set; } = [];

    // Never opted in, so it stays invisible exactly as an un-opted-in collection of rows does.
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// The element type of the one exposed collection. Carries no row policy — a collection of a policied
/// type is refused at startup, which <see cref="CollectionSubqueryTests"/> pins.
/// </summary>
[Queryable]
public class OrderLine
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }
}

/// <summary>
/// Was exposed as 'Issue' before the CLR type was renamed. Carries its row policy via the
/// [ReturnableWith] attribute rather than a programmatic AddPolicy, exercising the
/// attribute-discovery branch of Schema.ResolvePolicy.
/// </summary>
[Queryable]
[PreviousNames("Issue")]
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
/// contract. Adopting Name was itself a wire rename — it had been exposed as 'SalesRegion' — so the
/// old name is carried as a previous name. Has no DbSet; it exists to pin the naming behaviour.
/// </summary>
[Queryable(Name = "Region")]
[PreviousNames("SalesRegion")]
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

    // begin-snippet: obsoleteMember
    // Deprecated rather than removed: still queryable, still validated, still executed — clients are
    // only warned, at their next rebuild. [QueryIgnore] is what takes it off the surface for good.
    [Obsolete("Counts open roles too; use the Region rollup.")]
    public int Headcount { get; set; }
    // end-snippet
}

/// <summary>
/// Opted in with [Queryable] but marked EF [Keyless], which the schema treats as a view — the
/// documented equivalent of [QueryableView]. Pins that classification path. Deprecated with no
/// message, which reaches the client as a bare [Obsolete] on both the model and its entry point.
/// </summary>
[Queryable]
[Keyless]
[Obsolete]
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

/// <summary>
/// A TPH root that carries a row policy, with a derived type that opts in and carries one of its own.
/// This is the shape the inheritance guarantee is about: querying <see cref="Announcement"/> directly
/// must apply the base's policy as well as its own, or opting a subclass in would shed the base's.
/// Nothing else queries these, so the rows stay predictable.
/// </summary>
[Queryable]
[ReturnableWith(typeof(PublishedPostsOnlyPolicy))]
public class Post
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Published { get; set; }
}

[Queryable]
[ReturnableWith(typeof(PinnedAnnouncementsOnlyPolicy))]
public class Announcement : Post
{
    public bool Pinned { get; set; }
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

/// <summary>
/// Never attached by default. Registering it proves the startup refusal to expose a collection whose
/// element type is policied — <see cref="Order.Lines"/> is a collection of <see cref="OrderLine"/>.
/// </summary>
public sealed class BulkLinesOnlyPolicy :
    IReturnablePolicy<OrderLine>
{
    public IQueryable<OrderLine> Filter(IQueryable<OrderLine> source, ScryPolicyContext context) =>
        source.Where(_ => _.Quantity > 1);
}

/// <summary>
/// Never attached by default. Registering it proves the startup refusal to policy a complex type,
/// which is a member type with no source for a policy to filter — <see cref="Address"/> is reachable
/// only by traversing into it, or by aggregating <see cref="Employee.PreviousAddresses"/>.
/// </summary>
public sealed class UkAddressesOnlyPolicy :
    IReturnablePolicy<Address>
{
    public IQueryable<Address> Filter(IQueryable<Address> source, ScryPolicyContext context) =>
        source.Where(_ => _.Country == "UK");
}

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

/// <summary>The policy on the TPH root <see cref="Post"/>, inherited by <see cref="Announcement"/>.</summary>
public sealed class PublishedPostsOnlyPolicy :
    IReturnablePolicy<Post>
{
    public IQueryable<Post> Filter(IQueryable<Post> source, ScryPolicyContext context) =>
        source.Where(_ => _.Published);
}

/// <summary><see cref="Announcement"/>'s own policy, which narrows on top of the one it inherits.</summary>
public sealed class PinnedAnnouncementsOnlyPolicy :
    IReturnablePolicy<Announcement>
{
    public IQueryable<Announcement> Filter(IQueryable<Announcement> source, ScryPolicyContext context) =>
        source.Where(_ => _.Pinned);
}

/// <summary>
/// Never attached by default. Registered to prove an AddPolicy replaces the attribute on the type it
/// names without displacing what that type inherits.
/// </summary>
public sealed class AllAnnouncementsPolicy :
    IReturnablePolicy<Announcement>
{
    public IQueryable<Announcement> Filter(IQueryable<Announcement> source, ScryPolicyContext context) =>
        source;
}

/// <summary>
/// Never attached by default. Registered against the TPH root <see cref="Asset"/>, which carries no
/// attribute of its own, to prove a programmatic policy reaches the types deriving from it too. The
/// name it hides is the row the inheritance tests then look for.
/// </summary>
public sealed class VisibleAssetsOnlyPolicy :
    IReturnablePolicy<Asset>
{
    public IQueryable<Asset> Filter(IQueryable<Asset> source, ScryPolicyContext context) =>
        source.Where(_ => _.Name != "Trailer");
}

public sealed class TestContext(DbContextOptions<TestContext> options) :
    DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Post> Posts => Set<Post>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder) =>
        builder.Properties<decimal>().HavePrecision(18, 2);

    // Map the Address complex type into a JSON column, the scenario complex-type support targets.
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // begin-snippet: complexToJson
        builder.Entity<Employee>()
            .ComplexProperty(_ => _.Address)
            .ToJson();
        // end-snippet

        // begin-snippet: complexCollectionToJson
        builder.Entity<Employee>()
            .ComplexCollection(_ => _.PreviousAddresses)
            .ToJson();
        // end-snippet

        // Table-per-hierarchy: every derived type shares the base table and is told apart by a
        // discriminator, which is what OfType narrows on.
        builder.Entity<Vehicle>();
        builder.Entity<Building>();
        builder.Entity<Artwork>();
        builder.Entity<Announcement>();
    }

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

    /// <summary>
    /// A database of its own, for a test that needs data the shared seed does not have. The shared one
    /// is read-only by design — writing to it would leak into every other test in the assembly — so
    /// anything that has to insert builds its own and disposes it.
    /// </summary>
    public static Task<SqlDatabase<TestContext>> CreateIsolated(string name) =>
        sqlInstance.Build(name);

    static void Seed(TestContext context)
    {
        var engineering = new Department { Name = "Engineering" };
        var sales = new Department { Name = "Sales" };
        context.Departments.AddRange(engineering, sales);

        // PreviousAddresses is the JSON array the complex-collection tests read. Aaron's is deliberately
        // empty, so an aggregate over an empty array is covered the way the South order covers it for
        // an entity collection.
        var alice = new Employee
        {
            Name = "Alice", Status = Status.FullTime, Perks = Perks.Parking | Perks.Gym, Active = true, Department = engineering, Salary = 200_000, Avatar = [0x01, 0x02, 0x03],
            Address = new() { City = "London", Country = "UK", Zip = "EC1" },
            PreviousAddresses =
            [
                new() { City = "Berlin", Country = "DE", Zip = "10115" },
                new() { City = "Paris", Country = "FR", Zip = "75001" }
            ]
        };
        context.Employees.Add(alice);
        context.Employees.AddRange(
            new()
            {
                Name = "Aaron", Status = Status.FullTime, Perks = Perks.Gym, Active = true, Department = engineering, Manager = alice, Salary = 150_000, Avatar = [0x0A, 0x0B],
                Address = new() { City = "London", Country = "UK", Zip = "W1" }
            },
            new()
            {
                Name = "Bob", Status = Status.PartTime, Active = false, Department = sales, Manager = alice, Salary = 90_000, Avatar = [0xFF],
                Address = new() { City = "Berlin", Country = "DE", Zip = "10115" },
                PreviousAddresses = [new() { City = "London", Country = "UK", Zip = "EC2" }]
            },
            new()
            {
                Name = "Carol", Status = Status.Contractor, Perks = Perks.Remote | Perks.Gym, Active = true, Department = sales, Salary = 120_000, Avatar = [],
                Address = new() { City = "Paris", Country = "FR", Zip = "75001" },
                PreviousAddresses =
                [
                    new() { City = "London", Country = "UK", Zip = "SW1" },
                    new() { City = "Berlin", Country = "DE", Zip = "10117" }
                ]
            });

        context.Orders.AddRange(
            new()
            {
                Region = "North", Amount = 100m, Quantity = 3, Sku = 1000, Placed = new(2026, 3, 4, 9, 30, 15), Discount = 10m, Grade = 'A', Code = "40", Audited = "true",
                Lines =
                [
                    new() { Sku = "A-1", Quantity = 2, Price = 25m },
                    new() { Sku = "A-2", Quantity = 1, Price = 50m }
                ],
                Tags = ["urgent", "export"],
                Scores = [3, 5],
                Priorities = [Priority.High],
                Notes = ["hidden"]
            },
            // Sku is deliberately above long.MaxValue to prove the value survives the String-tag path
            // (a numeric Int64 tag would overflow).
            new()
            {
                Region = "North", Amount = 250m, Quantity = 7, Sku = ulong.MaxValue, Placed = new(2026, 7, 20, 14, 5, 0), Discount = null, Grade = 'B', Code = "8", Audited = "false",
                Lines = [new() { Sku = "B-1", Quantity = 5, Price = 50m }],
                Tags = ["export"],
                Scores = [8],
                Priorities = [Priority.Low, Priority.High]
            },
            // No lines, tags or scores at all, so an aggregate over an empty collection is covered for
            // both a collection of rows and one of values.
            new() { Region = "South", Amount = 75m, Quantity = 1, Sku = 3000, Placed = new(2025, 12, 31, 23, 59, 59), Discount = 5m, Grade = 'A', Code = "17", Audited = "true" });

        context.Assets.AddRange(
            new Vehicle {Name = "Van", Wheels = 4},
            new Vehicle {Name = "Trailer", Wheels = 2},
            new Building {Name = "Depot", Floors = 3},
            new Artwork {Name = "Mural", Medium = "Paint"});

        context.Tickets.AddRange(
            new() { Name = "Login bug", IsOpen = true },
            new() { Name = "Signup crash", IsOpen = true },
            new() { Name = "Old typo", IsOpen = false });

        // Each announcement fails a different one of the two policies, so which of them ran shows in
        // which rows come back. "Unpublished notice" is the row the base's policy exists to hide.
        context.Posts.AddRange(
            new Post { Name = "Draft post", Published = false },
            new Post { Name = "Live post", Published = true },
            new Announcement { Name = "Unpublished notice", Published = false, Pinned = true },
            new Announcement { Name = "Unpinned notice", Published = true, Pinned = false },
            new Announcement { Name = "Live notice", Published = true, Pinned = true });

        context.SaveChanges();
    }
}
