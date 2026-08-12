// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
// ReSharper disable NotAccessedPositionalProperty.Local

[TestFixture]
public class HttpRoundTripTests
{
    static readonly SqlInstance<Sample.Model.SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            Sample.Model.SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    WebApplication app = null!;
    HttpClient http = null!;
    ScryClient client = null!;
    ScryQuery query = null!;
    SqlDatabase<Sample.Model.SampleContext> database = null!;

    record EmployeeRow(string Name, Status Status, string? Manager, string Department);

    record RegionSummary(string Region, decimal Total, int Count);

    record HeadcountRow(string Department, int Headcount);

    record VehicleRow(string Name, int Wheels);

    record NameRow(string Name);

    record TaggedRegionRow(string Region, int Tags);

    static readonly string[] activeEmployeeNames = ["Aaron", "Alice", "Carol"];

    static readonly string[] departmentNames = ["Engineering", "Sales"];

    [OneTimeSetUp]
    public async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<Sample.Model.SampleContext>(options =>
        {
            options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
            options.MaxPageSize = 200;
            // Filters nothing, so every other test is unaffected; it is here to prove the header path
            // reaches a policy and back. Order is used rather than Employee because Employee is the
            // element of a [QueryableCollection], which refuses a policied element type at startup.
            options.AddPolicy<Sample.Model.Order, EchoHeaderPolicy>();
            // Department.Handbook is an [Attachment], and startup refuses a source whose attachment
            // nothing authorizes. No test here fetches it, so an allow-all satisfies the check.
            options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
        });

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        client = ScryClient.ForHttp(http, "/api/query");
        query = new(client);
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        await database.DisposeAsync();
    }

    [Test]
    public async Task EmployeesProjectionOverHttp()
    {
        var rows = await query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(activeEmployeeNames));
        Assert.That(rows[0].Manager, Is.EqualTo("Alice"));
        Assert.That(rows[1].Manager, Is.Null);
        Assert.That(rows[0].Department, Is.EqualTo("Engineering"));
    }

    [Test]
    public async Task ViewProjectionOverHttp()
    {
        // EmployeeSummary is a keyless [QueryableView] mapped to a SQL view. This confirms a view
        // round-trips the full pipeline: source discovery, validation, EF Set<T> against the view,
        // projection, and HTTP. The seed puts two employees in each of the two departments.
        var rows = await query.EmployeeSummary
            .OrderBy(_ => _.Department)
            .Select(_ => new HeadcountRow(_.Department, _.Headcount))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Department), Is.EqualTo(departmentNames));
        Assert.That(rows.Sum(_ => _.Headcount), Is.EqualTo(4));
    }

    [Test]
    public async Task ValueCollectionOverHttp()
    {
        // Order.Tags is a collection of values, which the generated model spells IReadOnlyList<string>.
        // This is the whole path for one: the generator read the element from the model DLL, the client
        // lowered Contains into a subquery over the element itself, and the server rebound it onto the
        // JSON column — with the stamp agreeing throughout, which is what GeneratedSchemaStampMatchesServer
        // then pins.
        var rows = await query.Order
            .Where(_ => _.Tags.Contains("urgent"))
            .Select(_ => new TaggedRegionRow(_.Region, _.Tags.Count))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single().Region, Is.EqualTo("North"));
            Assert.That(rows.Single().Tags, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task NarrowedToADerivedTypeOverHttp()
    {
        // Proves the whole hierarchy path end to end: the generated VehicleQueryModel inherits
        // AssetQueryModel, carries the wire source name the client narrows with, and the server
        // resolves that name through its own allow-list before executing the OfType.
        var rows = await query.Asset
            .OfType<VehicleQueryModel>()
            .OrderBy(_ => _.Name)
            .Select(_ => new VehicleRow(_.Name, _.Wheels))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Trailer", "Van"]));
            Assert.That(rows.Single(_ => _.Name == "Van").Wheels, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task NarrowedRowsProjectTheDerivedMembersByDefaultOverHttp()
    {
        // With no Select, the members projected come from the type the query narrowed to — not from
        // the source it started at, which knows nothing about Wheels.
        var rows = await query.Asset
            .OfType<VehicleQueryModel>()
            .OrderBy(_ => _.Name)
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Wheels), Is.EqualTo([2, 4]));
    }

    [Test]
    public async Task StreamedRowsOverHttp()
    {
        // The same request ToListAsync sends, read a row at a time off the streaming endpoint.
        // begin-snippet: clientStream
        var names = new List<string>();
        await foreach (var row in query.Employee
                           .Where(_ => _.Active)
                           .OrderBy(_ => _.Name)
                           .Select(_ => new NameRow(_.Name))
                           .ToAsyncEnumerable())
        {
            names.Add(row.Name);
        }
        // end-snippet

        Assert.That(names, Is.EqualTo(activeEmployeeNames));
    }

    [Test]
    public async Task StreamedRowsMatchTheListedOnesOverHttp()
    {
        var streamed = new List<string>();
        await foreach (var row in query
                           .Employee
                           .Select(_ => new NameRow(_.Name))
                           .ToAsyncEnumerable())
        {
            streamed.Add(row.Name);
        }

        var listed = await query.Employee.Select(_ => new NameRow(_.Name)).ToListAsync();

        Assert.That(streamed, Is.EqualTo(listed.Select(_ => _.Name)));
    }

    [Test]
    public void StreamingAQueryTheServerRejectsFailsBeforeAnyRowArrives()
    {
        // Validation runs to completion before anything is rebound, so a rejection is still a 400 with
        // a body rather than a stream that stops part-way.
        var exception = Assert.ThrowsAsync<ScryRequestException>(
            async () =>
            {
                await foreach (var _ in query.Employee
                                   .Select(row => new NameRow(row.Name))
                                   .Take(1_000_000)
                                   .ToAsyncEnumerable())
                {
                    Assert.Fail("No row should arrive from a rejected query.");
                }
            });

        Assert.That(exception!.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task GroupedAggregateOverHttp()
    {
        var regions = await query.Order
            .GroupBy(_ => _.Region)
            .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
            .ToListAsync();

        var north = regions.Single(_ => _.Region == "North");
        Assert.That(north.Total, Is.EqualTo(350m));
        Assert.That(north.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task CountOverHttp()
    {
        var count = await query.Employee
            .Where(_ => _.Active)
            .CountAsync();

        Assert.That(count, Is.EqualTo(3));
    }

    // The scenario a hand-rolled filter DTO (property name + operator enum + value, e.g.
    // AvnRepository's QueryFilter) exists for: criteria assembled at runtime — say from a grid's
    // filter UI — and sent to an API for EF to run. No DTO is needed here because capture is lazy:
    // nothing executes client-side, so operators appended conditionally at runtime are just more of
    // the captured pipeline, and the terminal serializes whatever was built as the wire AST. The
    // server validates it against the allow-list and runs it as SQL — no rows are loaded to filter
    // in memory, and no property-name strings are involved on the client.
    [Test]
    public async Task RuntimeComposedFilterOverHttp()
    {
        // begin-snippet: clientRuntimeComposition
        // Stand-ins for what a user typed into filter controls; unknowable at compile time.
        string? nameContains = "o";
        DateOnly? createdOnOrAfter = new(2026, 2, 1);
        var newestFirst = true;

        var employees = query.Employee;

        if (nameContains is { } contains)
        {
            employees = employees.Where(_ => _.Name.Contains(contains));
        }

        if (createdOnOrAfter is { } created)
        {
            employees = employees.Where(_ => _.Created >= created);
        }

        employees = newestFirst
            ? employees.OrderByDescending(_ => _.Created)
            : employees.OrderBy(_ => _.Created);

        var rows = await employees
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Carol", "Bob"]));
    }

    // begin-snippet: rawRequestRejected
    [Test]
    public async Task DisallowedPropertyRejectedWith400()
    {
        const string json = """
            {
              "version": 1,
              "root": "Employee",
              "pipeline": [
                {
                  "$type": "where",
                  "predicate": {
                    "$type": "binary",
                    "op": "GreaterThan",
                    "left": { "$type": "member", "path": ["Salary"] },
                    "right": { "$type": "const", "value": "100", "tag": "Decimal" }
                  }
                }
              ]
            }
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
    // end-snippet

    // The lockstep guarantee behind stale-client detection: the stamp the generator bakes into the
    // client (computed from Sample.Model's metadata on disk) must equal the stamp the server computes
    // from the same assembly via reflection. If the two surface readers ever diverge, this fails.
    [Test]
    public void GeneratedSchemaStampMatchesServer()
    {
        var processor = app.Services.GetRequiredService<ScryProcessor>();

        Assert.That(ScryQuery.SchemaStamp, Is.EqualTo(processor.Describe().SchemaStamp));
    }

    [Test]
    public async Task ResponseAdvertisesSchemaStamp()
    {
        using var content = new StringContent(
            """
            {
              "version": 1,
              "root": "Employee",
              "pipeline": [ { "$type": "count" } ]
            }
            """,
            Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync("/api/query", content);

        Assert.That(
            response.Headers.GetValues("Scry-Schema-Stamp").Single(),
            Is.EqualTo(ScryQuery.SchemaStamp));
    }

    // A client generated against the live model must never report itself stale — this is the
    // in-agreement half of the SchemaStale signal.
    [Test]
    public async Task MatchingClientIsNotReportedStale()
    {
        await query.Employee.CountAsync();

        Assert.That(client.ServerSchemaStamp, Is.EqualTo(ScryQuery.SchemaStamp));
        Assert.That(client.SchemaStale, Is.False);
    }

    // The drifted case: a client carrying a stamp from an older model learns it is stale from a
    // response header, even though the query itself succeeded.
    [Test]
    public async Task DriftedClientIsReportedStale()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();

        Assert.That(stale.SchemaStale, Is.True);
    }

    [Test]
    public async Task DriftedClientRaisesSchemaStaleDetected()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        SchemaDrift? drift = null;
        stale.SchemaStaleDetected += _ => drift = _;

        // The query itself succeeds — drift is reported alongside a working result, not as a failure.
        var count = await stale.Source<EmployeeQueryModel>("Employee").CountAsync();

        Assert.That(count, Is.EqualTo(4));
        Assert.That(drift, Is.Not.Null);
        Assert.That(drift!.ClientStamp, Is.EqualTo("stamp-from-an-older-model"));
        Assert.That(drift.ServerStamp, Is.EqualTo(ScryQuery.SchemaStamp));
    }

    // Raised once per client, however many queries follow: an app that polls would otherwise re-prompt
    // for a reload on every request until the user acts.
    [Test]
    public async Task SchemaStaleDetectedIsRaisedOnce()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        var raised = 0;
        stale.SchemaStaleDetected += _ => raised++;

        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();
        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();
        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();

        Assert.That(raised, Is.EqualTo(1));
    }

    // A client generated against the live model must stay silent — the half that keeps the signal from
    // being noise. Uses its own client so the subscription cannot leak into the shared fixture.
    [Test]
    public async Task MatchingClientNeverRaisesSchemaStaleDetected()
    {
        var current = ScryClient.ForHttp(http, "/api/query");
        var matching = new ScryQuery(current);

        var raised = false;
        current.SchemaStaleDetected += _ => raised = true;

        await matching.Employee.CountAsync();

        Assert.That(raised, Is.False);
        Assert.That(current.SchemaStale, Is.False);
    }

    // A drifted client whose query the server rejects gets a ScryStaleClientException — the same type
    // the payload reader throws for an unknown enum value — so one catch covers every stale-client
    // failure and can prompt a reload.
    [Test]
    public void DriftedClientRejectionThrowsStaleClientException()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        var exception = Assert.ThrowsAsync<ScryStaleClientException>(() =>
            stale.Source<EmployeeQueryModel>("Renamed").ToListAsync())!;

        Assert.That(exception.Message, Does.Contain("regenerate the client"));
    }

    // The wire shape behind it: the error body carries a structured staleClient marker, not just
    // prose, so non-.NET consumers can react without parsing the message.
    [Test]
    public async Task DriftedRejectionBodyCarriesStaleClientMarker()
    {
        using var content = new StringContent(
            """
            {
              "version": 1,
              "root": "Renamed",
              "pipeline": [ { "$type": "count" } ],
              "stamp": "stamp-from-an-older-model"
            }
            """,
            Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync("/api/query", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body, Does.Contain("\"staleClient\":true"));
    }

    // The same rejection without a stamp makes no staleness claim — the marker is omitted entirely
    // rather than sent as false.
    [Test]
    public async Task UnstampedRejectionBodyOmitsStaleClientMarker()
    {
        using var content = new StringContent(
            """
            {
              "version": 1,
              "root": "Renamed",
              "pipeline": [ { "$type": "count" } ]
            }
            """,
            Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync("/api/query", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body, Does.Not.Contain("staleClient"));
    }

    // A constant that fails to parse at rebind is also attributed: validation cannot catch a constant
    // aimed at a member whose type has since changed (constants are target-typed at rebind, not
    // type-checked by the validator), so the failure surfaces while the expression is being rebound —
    // far more likely a stale client than a hostile one. It is still a rejection: a 400 naming the
    // value, with the stale marker added.
    [Test]
    public async Task DriftedRebindFailureIsAttributedToStaleClient()
    {
        // Amount is decimal; "abc" passes validation and is rejected when ParseValue reconciles it
        // against the member's type.
        using var content = new StringContent(
            """
            {
              "version": 1,
              "root": "Order",
              "pipeline": [
                {
                  "$type": "where",
                  "predicate": {
                    "$type": "binary",
                    "op": "Equal",
                    "left": { "$type": "member", "path": ["Amount"] },
                    "right": { "$type": "const", "value": "abc", "tag": "String" }
                  }
                },
                { "$type": "count" }
              ],
              "stamp": "stamp-from-an-older-model"
            }
            """,
            Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync("/api/query", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body, Does.Contain("is not a valid Decimal value"));
        Assert.That(body, Does.Contain("regenerate the client"));
        Assert.That(body, Does.Contain("\"staleClient\":true"));
    }

    // A model frozen at a surface where ManagerId was still non-nullable. Alice has no manager, so the
    // server sends null and this cannot be read — the shape of drift the alias machinery cannot bridge
    // (nothing was renamed; a member changed).
    record PreNullableEmployee(string Name, int ManagerId);

    [Test]
    public void UnreadablePayloadFromDriftedClientThrowsStaleClientException()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        var exception = Assert.ThrowsAsync<ScryStaleClientException>(() =>
            stale.Source<PreNullableEmployee>("Employee", ["Name", "ManagerId"]).ToListAsync())!;

        Assert.That(exception.Message, Does.Contain("regenerate the client"));
        // The parse failure is preserved rather than replaced, so the cause stays diagnosable.
        Assert.That(exception.InnerException, Is.InstanceOf<JsonException>());
    }

    // The same unreadable payload from a client whose stamp agrees with the server is a real bug, not
    // drift — it must stay a raw parse failure rather than being dressed up as a reload prompt.
    [Test]
    public async Task UnreadablePayloadFromCurrentClientThrowsRawParseFailure()
    {
        var current = ScryClient.ForHttp(http, "/api/query");
        // Constructing ScryQuery stamps the client with the generated surface, which matches the server.
        _ = new ScryQuery(current);

        // Prime ServerSchemaStamp so SchemaStale is decided, not merely unknown.
        await current.Source<EmployeeQueryModel>("Employee").CountAsync();
        Assert.That(current.SchemaStale, Is.False);

        Assert.ThrowsAsync<JsonException>(() =>
            current.Source<PreNullableEmployee>("Employee", ["Name", "ManagerId"]).ToListAsync());
    }

    [Test]
    public void DisallowedPropertyThrowsThroughClient() =>
        // The generated client model has no Salary member (the server marks it [QueryIgnore]), so
        // attempts to reach hidden data must come as raw requests, which the server rejects (see the
        // 400 test). Here we confirm an unknown root is rejected through the typed client path.
        Assert.ThrowsAsync<ScryRequestException>(() =>
            client.Source<EmployeeQueryModel>("Secret").ToListAsync());

    // Several queries, one POST. What is being proved is that batching changes only how many requests
    // carry the queries: each entry arrives, validates, and comes back in the shape it would have had
    // on its own — including the result kinds differing between entries.
    [Test]
    public async Task BatchOverHttp()
    {
        var batch = client.Batch();

        var employees = query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .InBatch(batch)
            .ToListAsync();

        var departments = query.Department
            .InBatch(batch)
            .CountAsync();

        var first = query.Employee
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .InBatch(batch)
            .FirstOrDefaultAsync();

        await batch.SendAsync();

        Assert.That((await employees).Select(_ => _.Name), Is.EqualTo(activeEmployeeNames));
        Assert.That(await departments, Is.EqualTo(2));
        Assert.That((await first)!.Name, Is.EqualTo("Aaron"));
    }

    // A batch is not all-or-nothing: an entry the server refuses faults its own task and leaves the
    // rest of the batch answered, which is what makes it safe to put a page's queries in one.
    [Test]
    public async Task BatchEntryRejectedOverHttp()
    {
        var batch = client.Batch();

        var rejected = client.Source<EmployeeQueryModel>("Secret")
            .InBatch(batch)
            .ToListAsync();

        var accepted = query.Department
            .InBatch(batch)
            .CountAsync();

        await batch.SendAsync();

        var exception = Assert.ThrowsAsync<ScryRequestException>(async () => await rejected)!;
        Assert.That(exception.StatusCode, Is.EqualTo(400));
        Assert.That(await accepted, Is.EqualTo(2));
    }

    // The whole header path over real HTTP: the client attaches one, the server's row policy reads it
    // off ScryPolicyContext and answers on the response, and the client reads that back.
    [Test]
    public async Task HeadersRoundTripThroughAPolicyOverHttp()
    {
        string? echoed = null;

        var rows = await query.Order
            .WithHeader("X-Correlation", "round-trip-1")
            .OnResponseHeaders(_ => echoed = _.GetValues("X-Scry-Echo").Single())
            .Select(_ => new RegionRow(_.Region))
            .ToListAsync();

        Assert.That(rows, Is.Not.Empty);
        Assert.That(echoed, Is.EqualTo("round-trip-1"));
    }

    // The streaming endpoint commits its status and headers before the first row, so a policy's write
    // has to land ahead of that rather than after the response has started.
    [Test]
    public async Task HeadersRoundTripThroughAPolicyOverTheStreamingEndpoint()
    {
        string? echoed = null;
        var regions = new List<string>();

        await foreach (var row in query.Order
                           .WithHeader("X-Correlation", "round-trip-2")
                           .OnResponseHeaders(_ => echoed = _.GetValues("X-Scry-Echo").Single())
                           .Select(_ => new RegionRow(_.Region))
                           .ToAsyncEnumerable())
        {
            regions.Add(row.Region);
        }

        Assert.That(regions, Is.Not.Empty);
        Assert.That(echoed, Is.EqualTo("round-trip-2"));
    }

    // A rejected query still has response headers, and they are the ones worth reading.
    [Test]
    public void ResponseHeadersAreReadableOnARejectedQueryOverHttp()
    {
        string? stamp = null;

        var rejected = client.Source<EmployeeQueryModel>("Secret")
            .OnResponseHeaders(_ => stamp = _.GetValues(WireFormat.SchemaStampHeader).Single());

        Assert.ThrowsAsync<ScryRequestException>(() => rejected.ToListAsync());
        Assert.That(stamp, Is.EqualTo(ScryQuery.SchemaStamp));
    }

    // The client fingerprints the exact bytes it is about to send and carries the value in a header. The
    // policy reads it off the same ScryPolicyContext a correlation header arrives on, so a match here
    // proves it survived the round trip; comparing against the fingerprint of the serialized request
    // proves both sides agree on what was hashed, rather than merely on some value being present.
    [Test]
    public async Task QueryFingerprintReachesTheServerOverHttp()
    {
        string? received = null;

        var rows = await query.Order
            .OnResponseHeaders(_ => received = _.GetValues("X-Scry-Hash").Single())
            .Select(_ => new RegionRow(_.Region))
            .ToListAsync();

        var sent = QueryFingerprint.Compute(
            ScryJson.SerializeToUtf8(
                query.Order
                    .Select(_ => new RegionRow(_.Region))
                    .ToScryRequest()));

        Assert.That(rows, Is.Not.Empty);
        Assert.That(received, Is.EqualTo(sent));
    }

    record RegionRow(string Region);
}

/// <summary>
/// Filters nothing: it exists to prove a request header reaches a row policy and that a response
/// header written by one reaches the client. Echoing a client-chosen value back is safe; keying an
/// actual filter off one would not be, since the client controls it.
/// </summary>
public sealed class EchoHeaderPolicy :
    IReturnablePolicy<Sample.Model.Order>
{
    public IQueryable<Sample.Model.Order> Filter(IQueryable<Sample.Model.Order> source, ScryPolicyContext context)
    {
        context.ResponseHeaders["X-Scry-Echo"] = context.RequestHeaders["X-Correlation"];
        // The client's fingerprint of the request body, echoed the same way — this is a test double for a
        // server that would compare it against one of its own, not a suggestion that a policy should read
        // it. Nothing here filters on it; the client controls its value.
        context.ResponseHeaders["X-Scry-Hash"] = context.RequestHeaders[WireFormat.QueryHashHeader];
        return source;
    }
}

/// <summary>
/// Satisfies the mandatory attachment check for Department.Handbook; no test in this fixture
/// exercises the attachment endpoint itself — AttachmentTests covers that.
/// </summary>
public sealed class AllowAttachmentPolicy :
    IAttachmentPolicy<Sample.Model.Department>
{
    public bool Authorize(ScryAttachmentContext context) => true;
}
