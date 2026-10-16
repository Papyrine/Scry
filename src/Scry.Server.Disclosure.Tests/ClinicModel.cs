/// <summary>
/// The model these tests ask questions of: patients on wards, with one member nobody should be sent
/// without a record of it. Small on purpose — what is under test is what gets written down about an
/// answer, not the answer — and each source is here for the way its rows are, or are not, identified.
/// </summary>
[Queryable]
public class Patient
{
    // Hidden from clients, and still what a row is recorded by: the audit reads the key from the
    // model, not from what the allow-list lets a caller name.
    [QueryIgnore]
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [Sensitive]
    public string Diagnosis { get; set; } = "";

    public int WardId { get; set; }

    public Ward? Ward { get; set; }
}

[Queryable]
public class Ward
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>A row identified by two values together.</summary>
[Queryable]
[PrimaryKey(nameof(PatientId), nameof(Visit))]
public class Admission
{
    public int PatientId { get; set; }

    public int Visit { get; set; }

    public string Reason { get; set; } = "";
}

/// <summary>
/// Mapped with no key at all, as a view is: nothing in the model says what makes one row of it that
/// row, so the host has to, or has to say that nothing does.
/// </summary>
[QueryableView]
public class WardCensus
{
    public string Ward { get; set; } = "";

    public int Patients { get; set; }
}

/// <summary>
/// Keyed, by a value EF holds with no property to read it through. A row of it has an identity the
/// audit cannot name, which for a record is the same as having none.
/// </summary>
[Queryable]
public class Note
{
    public string Text { get; set; } = "";
}

public class ClinicContext(DbContextOptions<ClinicContext> options) :
    DbContext(options)
{
    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<Ward> Wards => Set<Ward>();

    public DbSet<Admission> Admissions => Set<Admission>();

    public DbSet<WardCensus> Census => Set<WardCensus>();

    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<WardCensus>().HasNoKey().ToTable("WardCensus");
        builder.Entity<Note>().Property<int>("Id");
        builder.Entity<Note>().HasKey("Id");
    }
}

/// <summary>
/// The clinic's database: a LocalDB instance of this project's own, and a database per test cloned
/// from one seeded template. The disclosure store's tables are made in the same database, which is
/// how a host is expected to run it.
/// </summary>
static class Clinic
{
    public static SqlInstance<ClinicContext> Instance { get; } = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: async context =>
        {
            await context.Database.EnsureCreatedAsync();
            var north = new Ward
            {
                Name = "North"
            };
            var south = new Ward
            {
                Name = "South"
            };
            var ada = new Patient
            {
                Name = "Ada",
                Diagnosis = "Fracture",
                Ward = north
            };
            var brook = new Patient
            {
                Name = "Brook",
                Diagnosis = "Asthma",
                Ward = north
            };
            var chidi = new Patient
            {
                Name = "Chidi",
                Diagnosis = "Fracture",
                Ward = south
            };
            context.Patients.AddRange(ada, brook, chidi);
            context.Notes.AddRange(
                new()
                {
                    Text = "Night round done"
                },
                new()
                {
                    Text = "Linen ordered"
                });
            await context.SaveChangesAsync();
            context.Admissions.AddRange(
                new()
                {
                    PatientId = ada.Id,
                    Visit = 1,
                    Reason = "Fall"
                },
                new()
                {
                    PatientId = ada.Id,
                    Visit = 2,
                    Reason = "Cast removed"
                },
                new()
                {
                    PatientId = chidi.Id,
                    Visit = 1,
                    Reason = "Fall"
                });
            await context.SaveChangesAsync();

            // A keyless type cannot be tracked, so its rows are written as the view's would be: by
            // something other than the context.
            await context.Database.ExecuteSqlRawAsync("INSERT INTO [WardCensus] ([Ward], [Patients]) VALUES (N'North', 2), (N'South', 1)");
        });

    /// <summary>The patients' names in order: three rows of one member.</summary>
    public static QueryRequest Names() =>
        QueryRequest.Create(
            "Patient",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

    /// <summary>Every row of a source, one member each, in that member's order.</summary>
    public static QueryRequest All(string source, string member) =>
        QueryRequest.Create(
            source,
            [
                new OrderByOp(new MemberNode([member]), Descending: false),
                new SelectOp(new([new(member, new NodeValue(new MemberNode([member])))]))
            ]);

    /// <summary>One small answer made by hand: a row, the unit that carried it, and its close.</summary>
    public static ScryDisclosureBatch Batch(int index)
    {
        var id = Guid.CreateVersion7();
        var bytes = Encoding.UTF8.GetBytes($$"""{"row":{{index}}}""");
        byte[] tagged = [(byte) ScryDisclosureContentKind.Row, .. bytes];
        var address = ScryDisclosureAddress.From(SHA256.HashData(tagged));
        return new()
        {
            EventId = id,
            Begin = new(id, DateTimeOffset.UtcNow, ScryDisclosureKind.Single, "Patient")
            {
                Caller = "dr.osei"
            },
            Units = [new(0, address)],
            Contents = [new(address, ScryDisclosureContentKind.Row, bytes.Length, bytes)],
            Close = new(ScryDisclosureOutcome.Released, 1)
        };
    }

    /// <summary>A piece of content made by hand, addressed as the audit addresses one with no key set.</summary>
    public static ScryDisclosureContent Content(ScryDisclosureContentKind kind, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        byte[] tagged = [(byte) kind, .. bytes];
        return new(ScryDisclosureAddress.From(SHA256.HashData(tagged)), kind, bytes.Length, bytes);
    }

    /// <summary>What a query's rows were made of, made by hand.</summary>
    public static ScryDisclosureShape Shape(params ScryDisclosureField[] fields)
    {
        var said = string.Join("|", fields.Select(_ => $"{_.Source}.{_.Member}.{_.Use}.{_.Sensitive}"));
        return new(Content(ScryDisclosureContentKind.Shape, said).Address, fields);
    }

    /// <summary>
    /// One batch of an answer made by hand: any of its header, its shape and its close, and the units
    /// it adds — each with what it carried and, where it was read from a row that has a key, that row.
    /// </summary>
    public static ScryDisclosureBatch Part(
        Guid id,
        int sequence,
        ScryDisclosureEvent? begin = null,
        ScryDisclosureShape? shape = null,
        ScryDisclosureClose? close = null,
        params (int Ordinal, ScryDisclosureContent Content, string? Source, string? Key)[] rows)
    {
        var entities = new List<ScryDisclosureEntity>();
        foreach (var (ordinal, _, source, key) in rows)
        {
            if (source is not null &&
                key is not null)
            {
                entities.Add(new(ordinal, 0, source, key, ""));
            }
        }

        return new()
        {
            EventId = id,
            Sequence = sequence,
            Begin = begin,
            Shape = shape,
            Units = [.. rows.Select(_ => new ScryDisclosureUnit(_.Ordinal, _.Content.Address))],
            Entities = entities,
            Contents = [.. rows.Select(_ => _.Content).DistinctBy(_ => _.Address)],
            Close = close
        };
    }

    /// <summary>
    /// What an answer was recorded as, a line per unit: what was sent, then the rows it was read from.
    /// </summary>
    public static async Task<List<string>> Recorded(ScryMemoryDisclosureStore store, string caller = "dr.osei")
    {
        var lines = new List<string>();
        var events = await store.ReceivedBy(caller, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        events.Reverse();
        foreach (var entry in events)
        {
            var answer = (await store.Reconstruct(entry.Event.Id))!;
            foreach (var unit in answer.Units)
            {
                var rows = string.Join(", ", unit.Entities.Select(_ => $"{_.Source}{_.Key}"));
                lines.Add($"{Encoding.UTF8.GetString(unit.Content.Bytes.Span)} <- {rows}");
            }
        }

        return lines;
    }
}
