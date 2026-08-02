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

public enum Grade
{
    Bronze,
    Silver,
    Gold
}

/// <summary>
/// Carries no sets — every source here is in memory. It exists because the schema is built from a
/// <see cref="DbContext"/> type's assembly. The connection string is deliberately unreachable, so a
/// benchmark that accidentally reached the database would fail rather than quietly measure I/O.
/// </summary>
public class BenchContext(DbContextOptions<BenchContext> options) :
    DbContext(options)
{
    public static DbContextOptions<BenchContext> Unreachable() =>
        new DbContextOptionsBuilder<BenchContext>()
            .UseSqlServer("Server=(localdb)\\scry-benchmarks-never-opens;Database=none")
            .Options;

    public static BenchContext Create() =>
        new(Unreachable());
}
