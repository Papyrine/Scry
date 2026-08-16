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
    static string? lastMethod;
    static readonly List<string> methods = [];

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

        // Records how each query actually travelled. The client chooses between a URL and a body by
        // length, and a test that only checked the rows could not tell which path produced them.
        app.Use(
            async (context, next) =>
            {
                lastMethod = context.Request.Method;
                methods.Add(lastMethod);
                await next();
            });

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

    /// <summary>
    /// Two streams read at once through one client. A client is registered per scope — which for a
    /// WASM app is the whole app — so the state a row is read with (the stream's enum aliases, and the
    /// binary parts belonging to that row) has to travel with the row rather than sit on the client,
    /// where a second enumeration would overwrite the first's between its yield and its read.
    /// </summary>
    [Test]
    public async Task ReadsTwoStreamsAtOnceThroughOneClient()
    {
        await using var employees = query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToAsyncEnumerable()
            .GetAsyncEnumerator();

        await using var departments = query.Department
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToAsyncEnumerable()
            .GetAsyncEnumerator();

        // Pulled alternately, so each row of one is read while the other stream is mid-flight.
        var fromEmployees = new List<string>();
        var fromDepartments = new List<string>();
        bool more;
        do
        {
            more = false;
            if (await employees.MoveNextAsync())
            {
                fromEmployees.Add(employees.Current.Name);
                more = true;
            }

            if (await departments.MoveNextAsync())
            {
                fromDepartments.Add(departments.Current.Name);
                more = true;
            }
        }
        while (more);

        Assert.Multiple(() =>
        {
            Assert.That(fromEmployees, Is.EqualTo(activeEmployeeNames));
            Assert.That(fromDepartments, Is.EqualTo(departmentNames));
        });
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
    // A query that fits in a URL is asked as one, so the caller's own HTTP cache can answer a repeat.
    // Same rows either way — the transport is the only thing that differs.
    [Test]
    public async Task SmallQueryTravelsAsAUrl()
    {
        var rows = await query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(lastMethod, Is.EqualTo("GET"));
        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(activeEmployeeNames));
    }

    // Past the length a URL can carry, the same query goes back to a body. The fallback is the whole
    // reason POST stays mapped: a URL has a ceiling and an IN list is the easiest way to reach it.
    [Test]
    public async Task OversizedQueryFallsBackToABody()
    {
        var ids = Enumerable.Range(0, 400)
            .Select(_ => $"tag-{_:D4}")
            .ToArray();

        var rows = await query.Order
            .Where(_ => ids.Contains(_.Region))
            .Select(_ => new NameRow(_.Region))
            .ToListAsync();

        Assert.That(lastMethod, Is.EqualTo("POST"));
        Assert.That(rows, Is.Empty);
    }

    // Employee.Password is [Sensitive], so the value compared against it never reaches a URL — where
    // it would be written to the access log of every hop between here and the server.
    [Test]
    public async Task SensitiveConstantTravelsAsABody()
    {
        var rows = await query.Employee
            .Where(_ => _.Password == "hunter2")
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(lastMethod, Is.EqualTo("POST"));
        Assert.That(rows, Is.Empty);
    }

    // Naming the same member without a constant leaves the transport alone: an ordering puts nothing
    // in the URL, so there is nothing to keep out of one.
    [Test]
    public async Task OrderingByASensitiveMemberKeepsTheUrl()
    {
        var rows = await query.Employee
            .OrderBy(_ => _.Password)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(lastMethod, Is.EqualTo("GET"));
        Assert.That(rows, Is.Not.Empty);
    }

    // The rule a client applies is the one the server holds it to. A hand-written request that broke
    // it — as a stale client's would — is refused, and refused in a way that says what to do instead.
    [Test]
    public async Task SensitiveConstantInAUrlIsRefused()
    {
        var encoded = QueryUrl.Encode(
            query.Employee
                .Where(_ => _.Password == "hunter2")
                .Select(_ => new NameRow(_.Name))
                .ToScryRequest());

        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");
        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsByteArrayAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error!.RequiresBody, Is.True);

            // Says what to do, never which member — a message naming it would answer "which of these
            // columns is the sensitive one?" for anyone who asked.
            Assert.That(error.Error, Does.Not.Contain("Password"));
            Assert.That(error.Error, Does.Contain("request body"));

            // And the refusal is never the thing a cache keeps.
            Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
        });
    }

    // The same query in a body is accepted, which is what makes the refusal above a retry rather than
    // a failure.
    [Test]
    public async Task SensitiveConstantInABodyIsAccepted()
    {
        var rows = await query.Employee
            .Where(_ => _.Password == "hunter2")
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(lastMethod, Is.EqualTo("POST"));
        Assert.That(rows, Is.Empty);
    }

    // Returning a sensitive member puts nothing in the URL, so the query keeps it — and the response
    // is marked unstorable, because `private, no-cache` would still write the rows to the caller's
    // disk. This is the half no client can opt out of.
    [Test]
    public async Task ProjectingASensitiveMemberIsNotStorable()
    {
        var encoded = QueryUrl.Encode(
            query.Employee
                .Where(_ => _.Active)
                .Select(_ => new PasswordRow(_.Password))
                .ToScryRequest());

        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
        });
    }

    // A query with no Select is answered with every member of the source, sensitive ones included, so
    // it is unstorable for the same reason without having named one.
    [Test]
    public async Task DefaultProjectionOfASensitiveSourceIsNotStorable()
    {
        var encoded = QueryUrl.Encode(query.Employee.Where(_ => _.Active).ToScryRequest());

        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
    }

    [Test]
    public async Task QueryTouchingNothingSensitiveStaysStorable()
    {
        var encoded = QueryUrl.Encode(
            query.Employee
                .Where(_ => _.Active)
                .Select(_ => new NameRow(_.Name))
                .ToScryRequest());

        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        Assert.Multiple(() =>
        {
            Assert.That(response.Headers.CacheControl!.NoStore, Is.False);
            Assert.That(response.Headers.CacheControl.Private, Is.True);
        });
    }

    record PasswordRow(string Password);

    // What a client generated before the member was marked does: it reads its own model, sees nothing
    // sensitive, and asks in a URL. The refusal is one it can act on without a person reading it, so
    // the query still returns — one round trip later, in a body — rather than failing.
    [Test]
    public async Task AClientThatDoesNotKnowRetriesInABody()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        methods.Clear();

        var rows = await stale.Source<UnmarkedEmployee>("Employee", ["Id", "Name", "Password"])
            .Where(_ => _.Password == "hunter2")
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        // Asked the way it believed it could, refused, and asked again the way it was told to — which
        // is the whole of the self-healing, and is why this is two requests rather than one.
        Assert.That(methods, Is.EqualTo(["GET", "POST"]));
        Assert.That(rows, Is.Empty);
    }

    // Deliberately without [ScrySensitive], which is what makes it stand for a client generated before
    // the model marked the member.
    [ScryModel("Employee", "Id", "Name", "Password")]
    public class UnmarkedEmployee
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Password { get; init; } = "";
    }

    // The URL is attacker-controlled like everything else on the wire, and fails closed: a parameter
    // that is not base64url of a request this server can parse is a 400, never a partial query.
    [Test]
    public async Task MalformedUrlQueryIsRejected()
    {
        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}=not-base64url!!");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Headers.GetValues("Scry-Schema-Stamp").Single(), Is.EqualTo(ScryQuery.SchemaStamp));
    }

    [Test]
    public async Task UrlQueryWithoutTheParameterIsRejected()
    {
        using var response = await http.GetAsync("/api/query");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // A URL identifies a response, so it may be stored — but only by the caller's own cache, and only
    // with a revalidation on every reuse. Rows are shaped by policies that read the request, so the
    // same URL answers differently for two principals.
    [Test]
    public async Task UrlQueryIsPrivatelyCacheable()
    {
        await query.Employee.Where(_ => _.Active).CountAsync();

        var encoded = QueryUrl.Encode(
            query.Employee.Where(_ => _.Active).ToScryRequest(new CountOp()));
        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        Assert.That(response.Headers.CacheControl!.Private, Is.True);
        Assert.That(response.Headers.CacheControl.NoCache, Is.True);
    }

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
