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

    // begin-snippet: binaryTransferMember
    // Travels as a raw multipart part in HTTP responses instead of base64 in the JSON payload, and —
    // being a photograph of a person — is never written to a cache that outlives the session reading it.
    [BinaryTransfer]
    [Sensitive]
    public byte[] Avatar { get; set; } = [];
    // end-snippet

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

    // An optional struct complex type: a Nullable<Workstation>, which every reader of a member path
    // unwraps to reach the struct's own members. Alice and Bob have one; the others read as null.
    public Workstation? Workstation { get; set; }

    [QueryIgnore]
    public decimal Salary { get; set; }
}

/// <summary>
/// The temporal types a date is not: an elapsed time, a bare date, a bare time, and an offset one.
/// Each maps to a column of its own on SQL Server, so the functions over them are exercised as
/// translated SQL rather than in memory. Carries a plain binary member for the same reason.
/// </summary>
/// <summary>
/// A base that never opted in. Its members are exposed on the derived type that did, as if declared
/// there: the generator reads a base in the model assembly whether or not it opted in, and the server
/// reads the same members by reflection. Not a source, and never an <c>OfType</c> target.
/// </summary>
public abstract class Audited
{
    public string CreatedBy { get; set; } = "";

    // Overridden below, so the member is declared twice in metadata and once in reflection; both
    // sides have to describe it once.
    public virtual string Notes { get; set; } = "";

    // Marked here and overridden below without the attribute. The generator carries the attributes
    // of every declaration along the chain onto the one member, so the server has to read through
    // the override too — or the override is sensitive to the client and not to the server.
    [Sensitive]
    public virtual string Reviewer { get; set; } = "";

    // The same shape for the hard stop: hidden on the base, overridden below, hidden on both sides.
    [QueryIgnore]
    public virtual string AuditTrail { get; set; } = "";
}

/// <summary>
/// Every shape the generator and the server once described differently, on one type, so
/// <c>LockstepTests</c> catches a divergence in any of them: an unannotated base, an override, an
/// indexer, and collections declared as arrays.
/// </summary>
[Queryable]
public class Invoice :
    Audited
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public override string Notes { get; set; } = "";
    public override string Reviewer { get; set; } = "";
    public override string AuditTrail { get; set; } = "";

    // An indexer is a property with parameters, which no query names and neither side exposes.
    public string this[int index] => Number;

    [QueryableCollection]
    public string[] Tags { get; set; } = [];

    [QueryableCollection]
    public int[] Weights { get; set; } = [];
}

[Queryable]
public class Shift
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // An elapsed time, whose parts are read in the plural — Hours, not Hour.
    public TimeSpan Duration { get; set; }

    public Date Day { get; set; }
    public Time Start { get; set; }
    public DateTimeOffset Stamped { get; set; }

    // A plain byte[]: neither an attachment nor a binary transfer, so it is an ordinary value a query
    // can ask questions about without ever reading.
    public byte[] Signature { get; set; } = [];
}

/// <summary>
/// A complex value type mapped to JSON. Opted in with [QueryableComplex]: reachable only by
/// traversing from <see cref="Employee"/> (e.g. Address.City), never as a root source. Zip is hidden.
/// </summary>
// begin-snippet: queryableComplex
[QueryableComplex]
[Sensitive]
public class Address
{
    public string City { get; set; } = "";
    public string Country { get; set; } = "";

    [QueryIgnore]
    public string Zip { get; set; } = "";
}
// end-snippet

/// <summary>
/// A complex type declared as a struct, mapped to JSON and optional on <see cref="Employee"/>. Its
/// extension reaches a person directly, so it is marked: a reader that lost the struct behind the
/// Nullable it travels in would let a constant compared against it into a URL.
/// </summary>
[QueryableComplex]
public struct Workstation
{
    public string Room { get; set; }

    [Sensitive]
    public string Extension { get; set; }
}

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
/// Owns a collection of <see cref="Machine"/>, whose element type carries no policy while an opted-in
/// subclass of it (<see cref="Press"/>) can be given one. That is the shape a flatten followed by a
/// narrowing has to apply the subclass's policy through. A hierarchy of its own rather than
/// <see cref="Asset"/>'s, whose types other fixtures attach policies to — a collection of a policied
/// element is refused at startup. No policy is attached by default, so the shared processor is
/// unchanged; <c>FlattenNarrowPolicyTests</c> registers what it needs.
/// </summary>
[Queryable]
public class Fleet
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [QueryableCollection]
    public List<Machine> Machines { get; set; } = [];
}

/// <summary>
/// The root of the fleet's TPH hierarchy. The foreign key is a real member, since a policied element
/// is read through the collection by correlating on it, which a shadow property cannot be.
/// </summary>
[Queryable]
public class Machine
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int FleetId { get; set; }
}

[Queryable]
public class Press : Machine
{
    public int Tonnage { get; set; }
}

/// <summary>Never attached by default. Hides the retired fleet, so the root of a flatten carries a policy.</summary>
public sealed class ActiveFleetsOnlyPolicy :
    IReturnablePolicy<Fleet>
{
    public IQueryable<Fleet> Filter(IQueryable<Fleet> source, ScryPolicyContext context) =>
        source.Where(_ => _.Name != "Retired");
}

/// <summary>Never attached by default. Hides the small press, the one row the light-press policy keeps.</summary>
public sealed class WorkingMachinesOnlyPolicy :
    IReturnablePolicy<Machine>
{
    public IQueryable<Machine> Filter(IQueryable<Machine> source, ScryPolicyContext context) =>
        source.Where(_ => _.Name != "Small press");
}

/// <summary>Never attached by default. Keeps the presses of a hundred tonnes and over.</summary>
public sealed class HeavyPressesOnlyPolicy :
    IReturnablePolicy<Press>
{
    public IQueryable<Press> Filter(IQueryable<Press> source, ScryPolicyContext context) =>
        source.Where(_ => _.Tonnage >= 100);
}

/// <summary>Never attached by default. The inverse, keeping the presses under a hundred tonnes.</summary>
public sealed class LightPressesOnlyPolicy :
    IReturnablePolicy<Press>
{
    public IQueryable<Press> Filter(IQueryable<Press> source, ScryPolicyContext context) =>
        source.Where(_ => _.Tonnage < 100);
}

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

    // The column a cached row policy reads to know a row needs deciding again. Server-side machinery
    // rather than query surface, so it is hidden like any other member Scry was not told to expose —
    // which is also what pins that a version column need not be one clients can see.
    [QueryIgnore]
    public long Revision { get; set; }

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

    // A narrow numeric on a collection element: no Sum or Average overload takes a short, so a fold
    // over it is what the server has to widen.
    public short Units { get; set; }

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

    // A Guid ordering key. Guid defines no relational operator, so a cursor seeking past one has to
    // compare it another way — which ClientRoundTripTests pins by paging through this on a real
    // database.
    public Guid Token { get; set; }
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
/// A POCO derived from <see cref="Holiday"/>, opted in with no registration of its own: its rows are
/// the base's narrowed by type. The shape a policy on a POCO base is reached through by narrowing,
/// where the retype runs over an in-memory query rather than a discriminator. The shared seed carries
/// none, so nothing else sees it; <c>PocoHierarchyTests</c> registers a seed that does.
/// </summary>
[QueryablePoco]
public class PublicHoliday : Holiday
{
    public string Region { get; set; } = "";

    public static IEnumerable<Holiday> SeedWithPublic() =>
    [
        .. Holiday.Seed(),
        new PublicHoliday { Name = "Anzac Day", Date = new(2026, 4, 25), Region = "AU" },
        new PublicHoliday { Name = "Unpublished day", Date = new(2026, 6, 1), Region = "AU" }
    ];
}

/// <summary>Never attached by default. Hides the holiday nobody has published yet.</summary>
public sealed class PublishedHolidaysOnlyPolicy :
    IReturnablePolicy<Holiday>
{
    public IQueryable<Holiday> Filter(IQueryable<Holiday> source, ScryPolicyContext context) =>
        source.Where(_ => !_.Name.StartsWith("Unpublished"));
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

/// <summary>
/// Carries an attachment, so it is the fixture for everything that reads one. Kept apart from
/// <see cref="Employee"/> deliberately: that type already carries a [BinaryTransfer] member and a row
/// policy in some tests, and an attachment answers to neither. The check comes from
/// [AttachmentWith] rather than a programmatic registration so every existing ScryProcessor.Create in
/// this assembly keeps starting unchanged.
/// </summary>
// begin-snippet: attachmentMember
[Queryable]
[AttachmentWith(typeof(UnsealedContractsPolicy))]
public class Contract
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Never read by a query. A client sees a handle and fetches the bytes by this row's key, and the
    // declared content type is what that fetch is served as.
    [Attachment(ContentType = "application/pdf")]
    public byte[]? Document { get; set; }
}
// end-snippet

/// <summary>
/// <see cref="Contract"/>'s attachment check: everything but the sealed contract, whose document is
/// refused however the row is reached.
/// </summary>
// begin-snippet: attachmentPolicy
public sealed class UnsealedContractsPolicy :
    IAttachmentPolicy<Contract>
{
    /// <summary>The seeded row this refuses, so a denial is exercised without needing a header.</summary>
    public const int SealedId = 3;

    public bool Authorize(ScryAttachmentContext context) =>
        context.KeyValues is not [SealedId];
}
// end-snippet

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

/// <summary>
/// Never attached by default. Takes a dependency and has no other constructor, so it can only be
/// built from a service provider that registers it — which the startup check has to notice, rather
/// than the first query of its source.
/// </summary>
public sealed class NeedsAClockPolicy(TimeProvider clock) :
    IReturnablePolicy<Order>
{
    public IQueryable<Order> Filter(IQueryable<Order> source, ScryPolicyContext context)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return source.Where(_ => _.Placed <= now);
    }
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

/// <summary>
/// Never attached by default. Denies the trailer, which is also the row
/// <see cref="VisibleAssetsOnlyPolicy"/> hides — so registering both proves a row already hidden is
/// never one a denial is reported for.
/// </summary>
public sealed class FourWheeledVehiclesOnlyPolicy :
    IReturnablePolicy<Vehicle>
{
    public IQueryable<Vehicle> Filter(IQueryable<Vehicle> source, ScryPolicyContext context) =>
        source.Where(_ => _.Wheels == 4);
}

/// <summary>
/// The inverse: denies the van, which <see cref="VisibleAssetsOnlyPolicy"/> allows. The one row left
/// visible being the one it denies is what makes a denial reportable at all.
/// </summary>
public sealed class TwoWheeledVehiclesOnlyPolicy :
    IReturnablePolicy<Vehicle>
{
    public IQueryable<Vehicle> Filter(IQueryable<Vehicle> source, ScryPolicyContext context) =>
        source.Where(_ => _.Wheels == 2);
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
    public DbSet<Fleet> Fleets => Set<Fleet>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<Invoice> Invoices => Set<Invoice>();

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

        builder.Entity<Employee>()
            .ComplexProperty(_ => _.Workstation)
            .ToJson();

        // The attachment tests fetch by key and the policy refuses one row by id, so the ids are seeded
        // explicitly rather than handed out by the identity column. The key itself stays the convention
        // one — only who assigns its value changes.
        builder.Entity<Contract>()
            .Property(_ => _.Id)
            .ValueGeneratedNever();

        // Table-per-hierarchy: every derived type shares the base table and is told apart by a
        // discriminator, which is what OfType narrows on.
        builder.Entity<Vehicle>();
        builder.Entity<Building>();
        builder.Entity<Artwork>();
        builder.Entity<Announcement>();

        builder.Entity<Press>();
        builder.Entity<Fleet>()
            .HasMany(_ => _.Machines)
            .WithOne()
            .HasForeignKey(_ => _.FleetId);
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
        var engineering = new Department
        {
            Name = "Engineering"
        };
        var sales = new Department
        {
            Name = "Sales"
        };
        context.Departments.AddRange(engineering, sales);

        // PreviousAddresses is the JSON array the complex-collection tests read. Aaron's is deliberately
        // empty, so an aggregate over an empty array is covered the way the South order covers it for
        // an entity collection.
        var alice = new Employee
        {
            Name = "Alice",
            Status = Status.FullTime,
            Perks = Perks.Parking | Perks.Gym,
            Active = true,
            Department = engineering,
            Salary = 200_000,
            Avatar = [0x01, 0x02, 0x03],
            Workstation = new()
            {
                Room = "3.14",
                Extension = "4471"
            },
            Address = new()
            {
                City = "London",
                Country = "UK",
                Zip = "EC1"
            },
            PreviousAddresses =
            [
                new()
                {
                    City = "Berlin",
                    Country = "DE",
                    Zip = "10115"
                },
                new()
                {
                    City = "Paris",
                    Country = "FR",
                    Zip = "75001"
                }
            ]
        };
        context.Employees.Add(alice);
        context.Employees.AddRange(
            new()
            {
                Name = "Aaron",
                Status = Status.FullTime,
                Perks = Perks.Gym,
                Active = true,
                Department = engineering,
                Manager = alice,
                Salary = 150_000,
                Avatar = [0x0A, 0x0B],
                Address = new()
                {
                    City = "London",
                    Country = "UK",
                    Zip = "W1"
                }
            },
            new()
            {
                Name = "Bob",
                Status = Status.PartTime,
                Active = false,
                Department = sales,
                Manager = alice,
                Salary = 90_000,
                Avatar = [0xFF],
                Workstation = new()
                {
                    Room = "1.02",
                    Extension = "4482"
                },
                Address = new()
                {
                    City = "Berlin",
                    Country = "DE",
                    Zip = "10115"
                },
                PreviousAddresses =
                [
                    new()
                    {
                        City = "London",
                        Country = "UK",
                        Zip = "EC2"
                    }
                ]
            },
            new()
            {
                Name = "Carol",
                Status = Status.Contractor,
                Perks = Perks.Remote | Perks.Gym,
                Active = true,
                Department = sales,
                Salary = 120_000,
                Avatar = [],
                Address = new()
                {
                    City = "Paris",
                    Country = "FR",
                    Zip = "75001"
                },
                PreviousAddresses =
                [
                    new()
                    {
                        City = "London",
                        Country = "UK",
                        Zip = "SW1"
                    },
                    new()
                    {
                        City = "Berlin",
                        Country = "DE",
                        Zip = "10117"
                    }
                ]
            });

        context.Orders.AddRange(
            new()
            {
                Region = "North",
                Amount = 100m,
                Revision = 1,
                Quantity = 3,
                Sku = 1000,
                Placed = new(2026, 3, 4, 9, 30, 15),
                Discount = 10m,
                Grade = 'A',
                Code = "40",
                Audited = "true",
                Lines =
                [
                    new()
                    {
                        Sku = "A-1",
                        Quantity = 2,
                        Price = 25m,
                        Units = 12
                    },
                    new()
                    {
                        Sku = "A-2",
                        Quantity = 1,
                        Price = 50m,
                        Units = 30
                    }
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
                Region = "North",
                Amount = 250m,
                Revision = 2,
                Quantity = 7,
                Sku = ulong.MaxValue,
                Placed = new(2026, 7, 20, 14, 5, 0),
                Discount = null,
                Grade = 'B',
                Code = "8",
                Audited = "false",
                Lines =
                [
                    new()
                    {
                        Sku = "B-1",
                        Quantity = 5,
                        Price = 50m,
                        Units = 7
                    }
                ],
                Tags = ["export"],
                Scores = [8],
                Priorities = [Priority.Low, Priority.High]
            },
            // No lines, tags or scores at all, so an aggregate over an empty collection is covered for
            // both a collection of rows and one of values.
            new()
            {
                Region = "South",
                Amount = 75m,
                Revision = 3,
                Quantity = 1,
                Sku = 3000,
                Placed = new(2025, 12, 31, 23, 59, 59),
                Discount = 5m,
                Grade = 'A',
                Code = "17",
                Audited = "true"
            });

        // Durations and stamps chosen so each part reads as a distinct number, and so the two rows
        // disagree on every one of them.
        context.Shifts.AddRange(
            new()
            {
                Name = "Early",
                Duration = new(7, 30, 15),
                Day = new(2026, 3, 4),
                Start = new(6, 15, 30),
                Stamped = new(2026, 3, 4, 6, 15, 30, TimeSpan.FromHours(2)),
                Signature = [0x0A, 0x0B, 0x0C]
            },
            new()
            {
                Name = "Late",
                Duration = new(9, 45, 50),
                Day = new(2026, 7, 19),
                Start = new(14, 5, 0),
                Stamped = new(2026, 7, 19, 14, 5, 0, TimeSpan.Zero),
                Signature = []
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
            },
            new Artwork
            {
                Name = "Mural",
                Medium = "Paint"
            });

        // A heavy press, a light one, and a plain machine in the active fleet; one more light press in
        // the retired fleet, which ActiveFleetsOnlyPolicy hides when a test attaches it.
        context.Fleets.AddRange(
            new()
            {
                Name = "Main",
                Machines =
                [
                    new Press
                    {
                        Name = "Big press",
                        Tonnage = 200
                    },
                    new Press
                    {
                        Name = "Small press",
                        Tonnage = 50
                    },
                    new()
                    {
                        Name = "Drill"
                    }
                ]
            },
            new()
            {
                Name = "Retired",
                Machines =
                [
                    new Press
                    {
                        Name = "Old press",
                        Tonnage = 50
                    }
                ]
            });

        // Tokens fixed rather than generated, so a snapshot ordered by one holds still.
        context.Tickets.AddRange(
            new()
            {
                Name = "Login bug",
                IsOpen = true,
                Token = new("11111111-1111-1111-1111-111111111111")
            },
            new()
            {
                Name = "Signup crash",
                IsOpen = true,
                Token = new("22222222-2222-2222-2222-222222222222")
            },
            new()
            {
                Name = "Old typo",
                IsOpen = false,
                Token = new("33333333-3333-3333-3333-333333333333")
            });

        // Each announcement fails a different one of the two policies, so which of them ran shows in
        // which rows come back. "Unpublished notice" is the row the base's policy exists to hide.
        context.Posts.AddRange(
            new Post
            {
                Name = "Draft post",
                Published = false
            },
            new Post
            {
                Name = "Live post",
                Published = true
            },
            new Announcement
            {
                Name = "Unpublished notice",
                Published = false,
                Pinned = true
            },
            new Announcement
            {
                Name = "Unpinned notice",
                Published = true,
                Pinned = false
            },
            new Announcement
            {
                Name = "Live notice",
                Published = true,
                Pinned = true
            });

        // Ids are assigned explicitly: the attachment tests fetch by key, and the policy refuses one of
        // them by id, so which row is which cannot be left to the identity column.
        context.Contracts.AddRange(
            new()
            {
                Id = 1,
                Name = "Lease",
                Document = [0x11, 0x22, 0x33]
            },
            // No document at all, which is the null a fetch answers with 204 rather than 404.
            new()
            {
                Id = 2,
                Name = "Draft",
                Document = null
            },
            new()
            {
                Id = UnsealedContractsPolicy.SealedId,
                Name = "Sealed",
                Document = [0x44]
            });

        context.SaveChanges();
    }
}
