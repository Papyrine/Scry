using Sample.Model;

/// <summary>
/// The 304 exchange the sample wires up: Delta's database timestamp and the client's query fingerprint
/// combined into an ETag, and what happens on the next identical query. See /docs/caching.md.
/// </summary>
[TestFixture]
public class ConditionalQueryTests
{
    ScryTestServer server = null!;

    [OneTimeSetUp]
    public async Task StartServer() =>
        // Its own server, not the shared one: this fixture writes to the database, and an ETag on
        // every response would be churn in the other fixtures' snapshots.
        server = await ScryTestServer.StartAsync(conditionalRequests: true);

    [OneTimeTearDown]
    public async Task StopServer() =>
        await server.DisposeAsync();

    [Test]
    public async Task RepeatedQueryIsNotModified()
    {
        var query = new ScryQuery(server.CreateScryClient());

        var etag = await Warm(query, "Engineering");

        // The same query, re-asked with what the server said last time. Headers are transport-only, so
        // the request bytes — and therefore the fingerprint the ETag was built from — are unchanged.
        var exception = Assert.ThrowsAsync<ScryRequestException>(
            () => Active(query, "Engineering")
                .WithHeader("If-None-Match", etag)
                .ToListAsync());

        // The raw client surfaces the 304 as a failure: on its own, a status with no body is not a
        // result it can materialize. QueryCacheHandler is what turns it into one — see below.
        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
    }

    // A bare "*" is not a match. The RFC reads it as "any current representation", which here would
    // answer 304 to a request whose query was never decoded — including one the validator would have
    // refused. A cache revalidates with the tag it holds, so nothing legitimate sends it.
    [Test]
    public async Task AWildcardConditionIsNotAMatch()
    {
        var query = new ScryQuery(server.CreateScryClient());
        await Warm(query, "Engineering");

        var rows = await Active(query, "Engineering")
            .WithHeader("If-None-Match", "*")
            .ToListAsync();

        Assert.That(rows, Is.Not.Empty);
    }

    // A rejection carries no ETag: a client that cached one could later be told its copy of the
    // rejection is still current. The tag is written only on a 200 that is not no-store.
    [Test]
    public async Task ARejectedQueryCarriesNoEtag()
    {
        using var http = server.CreateClient();
        var encoded = QueryUrl.Encode(QueryRequest.Create("Employee", [new WhereOp(new MemberNode(["Nope"])), new CountOp()]));
        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Headers.ETag, Is.Null);
            Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
        });
    }

    [Test]
    public async Task WriteToTheDatabaseInvalidatesTheEtag()
    {
        var query = new ScryQuery(server.CreateScryClient());

        var before = await Warm(query, "Sales");

        await using (var data = server.NewContext())
        {
            data.Employees.Add(
                new()
                {
                    Name = "Newcomer",
                    Active = true,
                    Status = Sample.Model.Status.FullTime,
                    Created = new(2026, 1, 1),
                    DepartmentId = data.Departments.OrderBy(_ => _.Id).First().Id
                });
            await data.SaveChangesAsync();

            await SettleAfterWrite(data);
        }

        string? after = null;
        await Active(query, "Sales")
            .WithHeader("If-None-Match", before)
            .OnResponseHeaders(_ => after = _.ETag?.ToString())
            .ToListAsync();

        // The row written above moved the database's timestamp, so the client's ETag no longer stands
        // for anything and the query is answered in full.
        Assert.That(after, Is.Not.Null.And.Not.EqualTo(before));
    }

    /// <summary>
    /// The trap a cached row policy sets for conditional requests, and what keeps it shut. Revoking a
    /// grant writes nothing to the database, so Delta's timestamp cannot move — and if that were all
    /// the ETag carried, the caller would be answered 304 and go on rendering the rows it had just
    /// lost, however promptly the policy itself was invalidated. The grants version is in
    /// <c>CacheScope</c> for exactly this, so the old ETag stops standing for anything.
    /// </summary>
    [Test]
    public async Task RevokingAGrantInvalidatesTheEtagWithoutAWrite()
    {
        var query = new ScryQuery(server.CreateScryClient());
        using var http = server.CreateClient();

        string? before = null;
        var granted = await Orders(query)
            .OnResponseHeaders(_ => before = _.ETag?.ToString())
            .ToListAsync();

        Assert.That(granted.Select(_ => _.Region), Does.Contain("South"));

        using (var revoke = await http.PostAsync("/api/grants/South?allowed=false", content: null))
        {
            revoke.EnsureSuccessStatusCode();
        }

        string? after = null;
        var revoked = await Orders(query)
            .WithHeader("If-None-Match", before!)
            .OnResponseHeaders(_ => after = _.ETag?.ToString())
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.Not.Null.And.Not.EqualTo(before), "the revoked caller's ETag still matched");
            Assert.That(revoked.Select(_ => _.Region), Does.Not.Contain("South"));
        });

        // Restored, since this fixture's server outlives the test.
        using var restore = await http.PostAsync("/api/grants/South?allowed=true", content: null);
        restore.EnsureSuccessStatusCode();
    }

    static IQueryable<RegionRow> Orders(ScryQuery query) =>
        query.Order
            .OrderBy(_ => _.Region)
            .Select(_ => new RegionRow(_.Region));

    record RegionRow(string Region);

    [Test]
    public async Task DifferentQueriesGetDifferentEtags()
    {
        var query = new ScryQuery(server.CreateScryClient());

        var engineering = await Warm(query, "Engineering");
        var sales = await Warm(query, "Sales");

        Assert.That(sales, Is.Not.EqualTo(engineering));

        // One query's ETag is never accepted for another: the fingerprint in it is of the request
        // bytes, and these two ask different things.
        string? answered = null;
        await Active(query, "Engineering")
            .WithHeader("If-None-Match", sales)
            .OnResponseHeaders(_ => answered = _.ETag?.ToString())
            .ToListAsync();

        Assert.That(answered, Is.EqualTo(engineering));
    }

    [Test]
    public async Task QueryInABodyGetsNoEtag()
    {
        var query = new ScryQuery(server.CreateScryClient());
        var body = ScryJson.SerializeToUtf8(Active(query, "Engineering").ToScryRequest());

        using var http = server.CreateClient();
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");

        // The same query, asked as a body rather than as a URL — which is what a query too long for a
        // URL does. Nothing identifies the request to a cache, so it is answered exactly as it would be
        // with no ETag wiring at all.
        using var response = await http.PostAsync("/api/query", content);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.ETag, Is.Null);
        });
    }

    [Test]
    public async Task ConditionalExchange()
    {
        var cache = new QueryCache();

        var services = new ServiceCollection();
        services.AddSingleton(cache);
        services.AddTransient<QueryCacheHandler>();
        var httpBuilder = services
            .AddHttpClient(
                "scry",
                _ => _.BaseAddress = new("http://localhost/"))
            .AddHttpMessageHandler<QueryCacheHandler>();

        // Below the cache handler, so the recording is of what actually crossed the wire rather than
        // of what the handler handed back.
        var recording = httpBuilder.AddRecording();
        httpBuilder.ConfigurePrimaryHttpMessageHandler(server.CreateHandler);

        await using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("scry");
        var query = new ScryQuery(ScryClient.ForHttp(http, "/api/query"));

        // Warmed through a client of its own, so the recorded pair starts where a real one does:
        // an empty cache. The warm-up itself matters because the first execution of a query shape can
        // move the database's timestamp by itself (statistics), which would make the pair below a miss
        // for a reason that has nothing to do with the exchange being shown.
        await Active(new(server.CreateScryClient()), "Engineering").ToListAsync();

        // And then waited on, because a warm-up only covers what this query does to the timestamp.
        // Another test in this fixture wrote to the same database, and the log position that Delta
        // reads keeps moving for a while after a write commits — long enough that the pair below can
        // straddle it and be answered in full for a reason the exchange is not about.
        await using (var data = server.NewContext())
        {
            await Settle(data);
        }

        var first = await Active(query, "Engineering").ToListAsync();
        var second = await Active(query, "Engineering").ToListAsync();

        Assert.Multiple(() =>
        {
            // The second query was never executed — the server only read its timestamp — and the rows
            // the caller got back are the first one's.
            Assert.That(second, Is.EqualTo(first));
            Assert.That(cache.Hits, Is.EqualTo(1));
            Assert.That(cache.Misses, Is.EqualTo(1));
        });

        // The ETag and the If-None-Match it comes back as are scrubbed: the value carries the
        // database's log position, which moves with every write the machine has ever done. That they
        // match is what the 304 below proves.
        await Verify(recording.Sends)
            .ScrubMember("ETag")
            .ScrubMember("If-None-Match");
    }

    /// <summary>
    /// Waits for the database's timestamp to catch up with a write that has already committed.
    /// </summary>
    /// <remarks>
    /// Not a test convenience: on SQL Server the log position Delta reads does not move the instant a
    /// transaction commits — on LocalDB it trails by a couple of hundred milliseconds — so for that
    /// long the previous ETag is still the current one and a repeated query is still answered 304.
    /// That window is a property of the approach, not of this test; /docs/caching.md says what it means
    /// for a read-after-write.
    /// </remarks>
    static async Task SettleAfterWrite(SampleContext data)
    {
        var written = await data.GetLastTimeStamp();
        var deadline = Stopwatch.StartNew();
        while (await data.GetLastTimeStamp() == written)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("The database timestamp never moved after a committed write.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Waits until the database's timestamp has held still for long enough to be relied on, rather than
    /// for it to move as <see cref="SettleAfterWrite"/> does.
    /// </summary>
    /// <remarks>
    /// The two are the same property from opposite sides: a write's log position does not appear
    /// instantly, and it does not stop moving instantly either. A test that asserts a 304 needs a
    /// timestamp that will still be the same one a moment later, which is not something a single read
    /// can tell — only a run of reads that agree.
    /// </remarks>
    static async Task Settle(SampleContext data)
    {
        var deadline = Stopwatch.StartNew();
        var last = await data.GetLastTimeStamp();
        var held = Stopwatch.StartNew();
        while (held.Elapsed < TimeSpan.FromMilliseconds(300))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("The database timestamp never stopped moving.");
            }

            await Task.Delay(50);

            var now = await data.GetLastTimeStamp();
            if (now != last)
            {
                last = now;
                held.Restart();
            }
        }
    }

    /// <summary>
    /// Runs a query once to settle the database, then again to capture the ETag the server minted for
    /// it. The first execution of a shape can move the timestamp by itself, and an ETag captured from
    /// that one would be stale before it was ever used.
    /// </summary>
    static async Task<string> Warm(ScryQuery query, string department)
    {
        await Active(query, department).ToListAsync();

        string? etag = null;
        await Active(query, department)
            .OnResponseHeaders(_ => etag = _.ETag?.ToString())
            .ToListAsync();

        Assert.That(etag, Is.Not.Null);
        return etag!;
    }

    static IQueryable<EmployeeRow> Active(ScryQuery query, string department) =>
        query.Employee
            .Where(_ => _.Active && _.Department!.Name == department)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name));

    public record EmployeeRow(string Name);
}
