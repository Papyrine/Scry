using Microsoft.EntityFrameworkCore;
using Scry;

namespace Benchmarks;

/// <summary>
/// The benchmark source, supplied in memory: the response benchmarks need real rows to shape and
/// serialize, and an in-memory source provides them without a database.
/// </summary>
[QueryablePoco]
public class MemRow
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Region { get; set; } = "";

    public Grade Grade { get; set; }

    public bool Active { get; set; }

    public decimal Amount { get; set; }

    public long Ticks { get; set; }

    public DateTime Created { get; set; }

    public double Score { get; set; }

    public static List<MemRow> Seed(int count)
    {
        string[] regions = ["North", "South", "East", "West"];
        var rows = new List<MemRow>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new()
            {
                Id = i,
                Name = $"Row {i}",
                Region = regions[i % regions.Length],
                Grade = (Grade)(i % 3),
                Active = i % 2 == 0,
                Amount = 10m + i,
                Ticks = 1_000_000L + i,
                Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                Score = i * 1.5
            });
        }

        return rows;
    }
}

/// <summary>
/// The renamed value gives the schema a non-empty enum-alias table, which is what routes a drifted
/// client onto the general fallback path <c>ResponseBenchmarks.Drifted</c> measures. It costs the
/// other arms nothing: the table rides the envelope only when a request's stamp disagrees.
/// </summary>
public enum Grade
{
    Bronze,

    [PreviousNames("Standard")]
    Silver,

    Gold
}

/// <summary>
/// An entity source for the preparation benchmarks. A query over it is composed through EF's own
/// provider, which is the provider the endpoint composes over — and which compiles nothing until the
/// query is enumerated, so a request can be prepared and never run. <see cref="Closed"/> is nullable
/// so a temporal read of it takes the unwrapping path; <see cref="TerritoryId"/> is what the join arm
/// joins on.
/// </summary>
[Queryable]
public class Account
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Region { get; set; } = "";

    public Grade Grade { get; set; }

    public bool Active { get; set; }

    public decimal Amount { get; set; }

    public DateTime Created { get; set; }

    public DateTime? Closed { get; set; }

    public int TerritoryId { get; set; }
}

/// <summary>The inner side of the join arm.</summary>
[Queryable]
public class Territory
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>
/// <see cref="Account"/> again, behind a row policy, so the policied arm measures a policy's
/// application and no other arm pays for one. The policy hides rather than fails, which is the
/// default, so no denied-row probe is planned for it.
/// </summary>
[Queryable]
[ReturnableWith(typeof(ActiveAccountsPolicy))]
public class GuardedAccount
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Region { get; set; } = "";

    public Grade Grade { get; set; }

    public bool Active { get; set; }

    public decimal Amount { get; set; }

    public DateTime Created { get; set; }

    public DateTime? Closed { get; set; }

    public int TerritoryId { get; set; }
}

public sealed class ActiveAccountsPolicy :
    IReturnablePolicy<GuardedAccount>
{
    public IQueryable<GuardedAccount> Filter(IQueryable<GuardedAccount> source, ScryPolicyContext context) =>
        source.Where(_ => _.Active);
}

/// <summary>
/// The schema is built from a <see cref="DbContext"/> type's assembly, and the preparation benchmarks
/// compose over its sets; every source the response benchmarks read is in memory. The connection
/// string is deliberately unreachable, so a benchmark that accidentally reached the database would
/// fail rather than quietly measure I/O.
/// </summary>
public class BenchContext(DbContextOptions<BenchContext> options) :
    DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Territory> Territories => Set<Territory>();

    public DbSet<GuardedAccount> GuardedAccounts => Set<GuardedAccount>();

    public static DbContextOptions<BenchContext> Unreachable() =>
        new DbContextOptionsBuilder<BenchContext>()
            .UseSqlServer("Server=(localdb)\\scry-benchmarks-never-opens;Database=none")
            .Options;

    public static BenchContext Create() =>
        new(Unreachable());
}
