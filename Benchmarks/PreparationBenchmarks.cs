using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scry;

namespace Benchmarks;

/// <summary>
/// What the server spends on a request before the database is asked: validating it, resolving its
/// source, applying its policies, rebinding it onto EF, and planning its projection. Nothing here is
/// executed, and nothing crosses HTTP — this is the work the endpoint does between reading a request
/// and handing a query to the provider.
/// </summary>
/// <remarks>
/// The arms prepare through <see cref="ScryProcessor.Stream(QueryRequest, DbContext, Cancel)"/>, which
/// builds the query and hands back its rows unread. The rows are never enumerated, so the provider
/// never compiles and never connects. The sources are entity sets rather than the in-memory rows the
/// response benchmarks read: composing over EF's provider is what the endpoint does, and an in-memory
/// provider compiles the whole tree on every enumeration, which would bury what is measured here. The
/// context's connection string is unreachable, so an arm that enumerated by mistake would fail rather
/// than quietly measure a round trip.
///
/// <c>Filtered</c> is the plain path and the baseline. The others each add one shape whose
/// preparation has a cost of its own: temporal reads, a membership list, a join, a row policy, a
/// deduplicated projection. <c>Translated</c> is the baseline carried on into EF's own pre-execution
/// work, so the server's share can be read against the provider's. <c>Deserialize</c> is the
/// request's JSON alone.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class PreparationBenchmarks
{
    ScryProcessor processor = null!;
    BenchContext db = null!;
    IServiceProvider services = null!;
    byte[] filteredJson = null!;
    QueryRequest filtered = null!;
    QueryRequest temporal = null!;
    QueryRequest inList = null!;
    QueryRequest joined = null!;
    QueryRequest policied = null!;
    QueryRequest distinctComposite = null!;

    [GlobalSetup]
    public void Setup()
    {
        processor = ScryProcessor.Create<BenchContext>(Configure);
        db = BenchContext.Create();
        services = new ServiceCollection().BuildServiceProvider();

        filtered = Requests.Filtered();
        filteredJson = Encoding.UTF8.GetBytes(ScryJson.Serialize(filtered));
        temporal = Requests.Temporal();
        inList = Requests.InList();
        joined = Requests.Joined();
        policied = Requests.Policied();
        distinctComposite = Requests.DistinctComposite();

        // Warm every arm, and hold each to the shape it names: the SQL a request would run is read
        // back without connecting, and an arm whose SQL lacks its operator would be measuring some
        // other path than the one it claims.
        Expect(filtered, "[Amount]");
        Expect(temporal, "DATEPART(year");
        Expect(inList, "IN (");
        Expect(joined, "JOIN");
        Expect(policied, "[Active]");
        Expect(distinctComposite, "DISTINCT");
    }

    static void Configure(ScryOptions options) =>
        options.AddPocoSource(_ => MemRow.Seed(1));

    void Expect(QueryRequest request, string fragment)
    {
        var sql = processor.ToQueryString(request, db, services);
        if (!sql.Contains(fragment))
        {
            throw new($"The prepared query does not contain '{fragment}', so the arm is not measuring the shape it names:{Environment.NewLine}{sql}");
        }

        // Warmed the way the arm runs it, not only through the SQL preview.
        processor.Stream(request, db);
    }

    [GlobalCleanup]
    public void Cleanup() =>
        db.Dispose();

    [Benchmark(Description = "the request's JSON alone")]
    public QueryRequest Deserialize() =>
        ScryJson.DeserializeRequest(filteredJson);

    [Benchmark(Baseline = true, Description = "a predicate and a projection")]
    public ScryStreamMarker Filtered() =>
        processor.Stream(filtered, db).Begin;

    [Benchmark(Description = "temporal reads, one through a nullable")]
    public ScryStreamMarker Temporal() =>
        processor.Stream(temporal, db).Begin;

    [Benchmark(Description = "a membership list")]
    public ScryStreamMarker InList() =>
        processor.Stream(inList, db).Begin;

    [Benchmark(Description = "an inner join")]
    public ScryStreamMarker Joined() =>
        processor.Stream(joined, db).Begin;

    [Benchmark(Description = "a row policy")]
    public ScryStreamMarker Policied() =>
        processor.Stream(policied, db).Begin;

    [Benchmark(Description = "a deduplicated projection, ordered")]
    public ScryStreamMarker DistinctComposite() =>
        processor.Stream(distinctComposite, db).Begin;

    [Benchmark(Description = "the baseline carried into EF's translation")]
    public string Translated() =>
        processor.ToQueryString(filtered, db, services);
}
