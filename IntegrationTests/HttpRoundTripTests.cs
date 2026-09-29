// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
// ReSharper disable NotAccessedPositionalProperty.Local

[NotInParallel]
[DependsOn<StaleClient>(nameof(StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
public class HttpRoundTripTests
{
    static readonly SqlInstance<Sample.Model.SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            Sample.Model.SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    static WebApplication app = null!;
    static HttpClient http = null!;
    static ScryClient client = null!;
    static ScryQuery query = null!;
    static SqlDatabase<Sample.Model.SampleContext> database = null!;
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

    [Before(Class)]
    public static async Task StartServer()
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
            options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
        });

        app = builder.Build();

        // Records how each query actually travelled. The client chooses between a URL and a body by
        // length, and a test that only checked the rows could not tell which path produced them.
        app.Use((context, next) =>
            {
                lastMethod = context.Request.Method;
                methods.Add(lastMethod);
                return next();
            });

        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        client = ScryClient.ForHttp(http, "/api/query");
        query = new(client);
    }

    [After(Class)]
    public static async Task StopServer()
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

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(activeEmployeeNames, CollectionOrdering.Matching);
        await Assert.That(rows[0].Manager).IsEqualTo("Alice");
        await Assert.That(rows[1].Manager).IsNull();
        await Assert.That(rows[0].Department).IsEqualTo("Engineering");
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

        await Assert.That(rows.Select(_ => _.Department)).IsEquivalentTo(departmentNames, CollectionOrdering.Matching);
        await Assert.That(rows.Sum(_ => _.Headcount)).IsEqualTo(4);
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

        using (Assert.Multiple())
        {
            await Assert.That(rows.Single().Region).IsEqualTo("North");
            await Assert.That(rows.Single().Tags).IsEqualTo(2);
        }
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

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Trailer", "Van"], CollectionOrdering.Matching);
            await Assert.That(rows.Single(_ => _.Name == "Van").Wheels).IsEqualTo(4);
        }
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

        await Assert.That(rows.Select(_ => _.Wheels)).IsEquivalentTo([2, 4], CollectionOrdering.Matching);
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

        await Assert.That(names).IsEquivalentTo(activeEmployeeNames, CollectionOrdering.Matching);
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

        await Assert.That(streamed).IsEquivalentTo(listed.Select(_ => _.Name), CollectionOrdering.Matching);
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
            // ReSharper disable once MethodSupportsCancellation
            .GetAsyncEnumerator();

        await using var departments = query.Department
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToAsyncEnumerable()
            // ReSharper disable once MethodSupportsCancellation
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

        using (Assert.Multiple())
        {
            await Assert.That(fromEmployees).IsEquivalentTo(activeEmployeeNames, CollectionOrdering.Matching);
            await Assert.That(fromDepartments).IsEquivalentTo(departmentNames, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task StreamingAQueryTheServerRejectsFailsBeforeAnyRowArrives()
    {
        // Validation runs to completion before anything is rebound, so a rejection is still a 400 with
        // a body rather than a stream that stops part-way.
        var exception = await Assert.ThrowsExactlyAsync<ScryRequestException>(
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

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    record RatioRow(int Ratio);

    // A provider failure once rows are already on the wire: the status is long since sent, so the
    // failure rides the stream's error marker — carrying the fixed message, never the provider's
    // text, exactly as a non-streamed 500 would. Dividing by a client constant is the reliable way
    // to fail on the second row rather than the first, which is what proves the stream had begun.
    [Test]
    public async Task AProviderFailureAfterTheStreamBeganEndsItWithTheFixedMessage()
    {
        var rows = new List<RatioRow>();
        var exception = await Assert.ThrowsExactlyAsync<ScryWireException>(
            async () =>
            {
                await foreach (var row in query.Employee
                                   .OrderBy(_ => _.Id)
                                   .Select(_ => new RatioRow(100 / (_.Id - 2)))
                                   .ToAsyncEnumerable())
                {
                    rows.Add(row);
                }
            });

        using (Assert.Multiple())
        {
            await Assert.That(rows).IsNotEmpty().Because("the stream should have begun before the failure");
            await Assert.That(exception!.Message).Contains("Query execution failed.");
            await Assert.That(exception.Message).DoesNotContain("zero").IgnoringCase();
        }
    }

    [Test]
    public async Task GroupedAggregateOverHttp()
    {
        var regions = await query.Order
            .GroupBy(_ => _.Region)
            .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
            .ToListAsync();

        var north = regions.Single(_ => _.Region == "North");
        await Assert.That(north.Total).IsEqualTo(350m);
        await Assert.That(north.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CountOverHttp()
    {
        var count = await query.Employee
            .Where(_ => _.Active)
            .CountAsync();

        await Assert.That(count).IsEqualTo(3);
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
        var nameContains = "o";
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

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Carol", "Bob"], CollectionOrdering.Matching);
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
                    "left": { "$type": "member", "path": "Salary" },
                    "right": { "$type": "const", "value": "100", "tag": "Decimal" }
                  }
                }
              ]
            }
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
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

        await Assert.That(lastMethod).IsEqualTo("GET");
        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(activeEmployeeNames, CollectionOrdering.Matching);
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

        await Assert.That(lastMethod).IsEqualTo("POST");
        await Assert.That(rows).IsEmpty();
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

        await Assert.That(lastMethod).IsEqualTo("POST");
        await Assert.That(rows).IsEmpty();
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

        await Assert.That(lastMethod).IsEqualTo("GET");
        await Assert.That(rows).IsNotEmpty();
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

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(error!.RequiresBody).IsTrue();

            // Says what to do, never which member — a message naming it would answer "which of these
            // columns is the sensitive one?" for anyone who asked.
            await Assert.That(error.Error).DoesNotContain("Password");
            await Assert.That(error.Error).Contains("request body");

            // And the refusal is never the thing a cache keeps.
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        }
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

        await Assert.That(lastMethod).IsEqualTo("POST");
        await Assert.That(rows).IsEmpty();
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

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        }
    }

    // A query with no Select is answered with every member of the source, sensitive ones included, so
    // it is unstorable for the same reason without having named one.
    [Test]
    public async Task DefaultProjectionOfASensitiveSourceIsNotStorable()
    {
        var encoded = QueryUrl.Encode(query.Employee.Where(_ => _.Active).ToScryRequest());

        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
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

        using (Assert.Multiple())
        {
            await Assert.That(response.Headers.CacheControl!.NoStore).IsFalse();
            await Assert.That(response.Headers.CacheControl.Private).IsTrue();
        }
    }

    record PasswordRow(string Password);

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

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.Headers.GetValues("Scry-Schema-Stamp").Single()).IsEqualTo(ScryQuery.SchemaStamp);
    }

    // Every way a URL can fail to be a request is one 400: not base64url, base64url of something that
    // is not JSON, and JSON that is not a request. None reaches a handler, and none is storable.
    [Test]
    [Arguments("not-base64url!!", DisplayName = "not base64url")]
    [Arguments("bm90IGpzb24", DisplayName = "base64url of text that is not JSON")]
    [Arguments("eyJhIjoxfQ", DisplayName = "base64url of JSON that is not a request")]
    public async Task AUrlThatDoesNotDecodeToARequestIsRejected(string encoded)
    {
        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}");

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        }
    }

    // Two q parameters join into one string that is not base64url: a 400, not the first one.
    [Test]
    public async Task ADoubledQueryParameterIsRejected()
    {
        var encoded = QueryUrl.Encode(QueryRequest.Create("Employee", [new CountOp()]));
        using var response = await http.GetAsync($"/api/query?{QueryUrl.Parameter}={encoded}&{QueryUrl.Parameter}={encoded}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // Only the methods with a handler are mapped, so anything else is routing's 405 — no handler runs
    // and nothing is advertised beyond the Allow header routing writes.
    [Test]
    [Arguments("HEAD")]
    [Arguments("OPTIONS")]
    [Arguments("PUT")]
    public async Task OtherMethodsOnTheQueryRouteAreNotAllowed(string method)
    {
        using var request = new HttpRequestMessage(new(method), "/api/query");
        using var response = await http.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
    }

    // A batch is one response, so a header one entry's policy writes is on it once — for the whole
    // batch, since the entries share a response. Pinned as intended.
    [Test]
    public async Task AHeaderWrittenByOneEntryIsOnTheWholeBatchResponse()
    {
        var batch = QueryBatchRequest.Create([
            QueryRequest.Create("Order", [new CountOp()]),
            QueryRequest.Create("Department", [new CountOp()])
        ]);
        using var content = new StringContent(ScryJson.Serialize(batch), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/query/batch") {Content = content};
        request.Headers.Add("X-Correlation", "batch-1");

        using var response = await http.SendAsync(request);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.GetValues("X-Scry-Echo").Single()).IsEqualTo("batch-1");
        }
    }

    // A body missing a member an operator requires is refused where an unparseable one is: a 400
    // naming the member, never a server fault a validator reached by dereferencing the default.
    [Test]
    public async Task IncompleteBodyIsRejected()
    {
        var json =
            """
            {
              "version": 1,
              "root": "Employee",
              "pipeline": [
                {
                  "$type": "where"
                }
              ]
            }
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsByteArrayAsync());
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(error!.Error).Contains("predicate");
        }
    }

    // JSON nesting is bounded by the reader — 64 levels, its default — before the expression depth
    // limit could be reached, so a document nested past it is a malformed body: a 400 naming the
    // depth, never a reader fault or a stack the validator has to unwind.
    [Test]
    public async Task ADeeplyNestedBodyIsRejected()
    {
        var predicate = """{"$type":"member","path":"Active"}""";
        for (var i = 0; i < 100; i++)
        {
            predicate = $$"""{"$type":"unary","op":"Not","operand":{{predicate}}}""";
        }

        var json = $$"""{"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{{predicate}}}]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsByteArrayAsync());
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(error!.Error).Contains("depth");
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        }
    }

    // A body that is not declared JSON is refused before it is read. An HTML form can navigate a
    // browser to a POST endpoint with a text/plain field shaped as JSON — exactly the body below —
    // but it cannot set application/json, so requiring the header is what keeps a cross-site page
    // from executing a query as whoever the browser sent.
    [Test]
    [Arguments("/api/query")]
    [Arguments("/api/query/stream")]
    [Arguments("/api/query/batch")]
    [Arguments("/api/query/attachment")]
    public async Task ABodyThatIsNotJsonIsRefused(string endpoint)
    {
        var body = "{\"version\":1,\"root\":\"Employee\",\"pipeline\":[{\"$type\":\"count\"}],\"pad\":\"=\"}\r\n";
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var response = await http.PostAsync(endpoint, content);
        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsByteArrayAsync());

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
            await Assert.That(error!.Error).Contains("application/json");
            await Assert.That(error.Code).IsEqualTo(ScryErrorCode.UnsupportedMedia);
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        }
    }

    /// <summary>
    /// Every answer the endpoint gives carries the code for what it is, so a caller separates "this
    /// query is invalid, never retry" from "execution failed, maybe transient" without matching on a
    /// message the server deliberately keeps uninformative.
    /// </summary>
    [Test]
    public async Task EachAnswerCarriesItsOwnCode()
    {
        var malformed = await Code(http.PostAsync("/api/query", Json("{\"version\":1,\"root\":")));

        // Read as a request, refused by the allow-list.
        var rejected = await Code(
            http.PostAsync(
                "/api/query",
                Json("""{"version":1,"root":"Nope","pipeline":[{"$type":"count"}]}""")));

        using (Assert.Multiple())
        {
            await Assert.That(malformed).IsEqualTo(ScryErrorCode.WireFormat);
            await Assert.That(rejected).IsEqualTo(ScryErrorCode.Validation);
        }

        return;

        static StringContent Json(string body) =>
            new(body, Encoding.UTF8, "application/json");

        static async Task<ScryErrorCode?> Code(Task<HttpResponseMessage> send)
        {
            using var response = await send;
            return ScryJson.TryDeserializeError(await response.Content.ReadAsByteArrayAsync())?.Code;
        }
    }

    [Test]
    [Arguments("multipart/form-data")]
    [Arguments("application/x-www-form-urlencoded")]
    public async Task AFormBodyIsRefused(string mediaType)
    {
        using var content = new StringContent("{}", Encoding.UTF8, mediaType);
        using var response = await http.PostAsync("/api/query", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
    }

    [Test]
    public async Task ABodyWithNoContentTypeIsRefused()
    {
        using var content = new ByteArrayContent("{}"u8.ToArray());
        content.Headers.ContentType = null;
        using var response = await http.PostAsync("/api/query", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
    }

    // The two spellings a JSON client sends, with and without a charset, are both JSON.
    [Test]
    [Arguments("application/json")]
    [Arguments("application/json; charset=utf-8")]
    public async Task AJsonBodyIsAccepted(string contentType)
    {
        using var content = new ByteArrayContent(
            ScryJson.SerializeToUtf8(query.Employee.ToScryRequest(new CountOp())));
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        using var response = await http.PostAsync("/api/query", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task UrlQueryWithoutTheParameterIsRejected()
    {
        using var response = await http.GetAsync("/api/query");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
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

        await Assert.That(response.Headers.CacheControl!.Private).IsTrue();
        await Assert.That(response.Headers.CacheControl.NoCache).IsTrue();
    }

    [Test]
    public async Task GeneratedSchemaStampMatchesServer()
    {
        var processor = app.Services.GetRequiredService<ScryProcessor>();

        await Assert.That(ScryQuery.SchemaStamp).IsEqualTo(processor.Describe().SchemaStamp);
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

        await Assert.That(response.Headers.GetValues("Scry-Schema-Stamp").Single()).IsEqualTo(ScryQuery.SchemaStamp);
    }

    // A client generated against the live model must never report itself stale — this is the
    // in-agreement half of the SchemaStale signal.
    [Test]
    public async Task MatchingClientIsNotReportedStale()
    {
        await query.Employee.CountAsync();

        await Assert.That(client.ServerSchemaStamp).IsEqualTo(ScryQuery.SchemaStamp);
        await Assert.That(client.SchemaStale).IsFalse();
    }

    // The drifted case: a client carrying a stamp from an older model learns it is stale from a
    // response header, even though the query itself succeeded.
    [Test]
    public async Task DriftedClientIsReportedStale()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        await stale.Source<EmployeeQueryModel>("Employee").CountAsync();

        await Assert.That(stale.SchemaStale).IsTrue();
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

        await Assert.That(count).IsEqualTo(4);
        await Assert.That(drift).IsNotNull();
        await Assert.That(drift!.ClientStamp).IsEqualTo("stamp-from-an-older-model");
        await Assert.That(drift.ServerStamp).IsEqualTo(ScryQuery.SchemaStamp);
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

        await Assert.That(raised).IsEqualTo(1);
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

        await Assert.That(raised).IsFalse();
        await Assert.That(current.SchemaStale).IsFalse();
    }

    // A drifted client whose query the server rejects gets a ScryStaleClientException — the same type
    // the payload reader throws for an unknown enum value — so one catch covers every stale-client
    // failure and can prompt a reload.
    [Test]
    public async Task DriftedClientRejectionThrowsStaleClientException()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        var exception = (await Assert.ThrowsExactlyAsync<ScryStaleClientException>(() =>
            stale.Source<EmployeeQueryModel>("Renamed").ToListAsync()))!;

        await Assert.That(exception.Message).Contains("regenerate the client");
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

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body).Contains("\"code\":\"StaleClient\"");
    }

    // The same rejection without a stamp makes no staleness claim: it is coded for what it is, an
    // ordinary rejection, since there is no stamp to attribute it with.
    [Test]
    public async Task UnstampedRejectionBodyIsCodedAsAPlainValidationFailure()
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

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body).Contains("\"code\":\"Validation\"");
        await Assert.That(body).DoesNotContain("StaleClient");
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
                    "left": { "$type": "member", "path": "Amount" },
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

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body).Contains("is not a valid Decimal value");
        await Assert.That(body).Contains("regenerate the client");
        await Assert.That(body).Contains("\"code\":\"StaleClient\"");
    }

    // A model frozen at a surface where ManagerId was still non-nullable. Alice has no manager, so the
    // server sends null and this cannot be read — the shape of drift the alias machinery cannot bridge
    // (nothing was renamed; a member changed).
    record PreNullableEmployee(string Name, int ManagerId);

    [Test]
    public async Task UnreadablePayloadFromDriftedClientThrowsStaleClientException()
    {
        var stale = ScryClient.ForHttp(http, "/api/query");
        stale.SchemaStamp = "stamp-from-an-older-model";

        var exception = (await Assert.ThrowsExactlyAsync<ScryStaleClientException>(() =>
            stale.Source<PreNullableEmployee>("Employee", ["Name", "ManagerId"]).ToListAsync()))!;

        await Assert.That(exception.Message).Contains("regenerate the client");
        // The parse failure is preserved rather than replaced, so the cause stays diagnosable.
        await Assert.That(exception.InnerException).IsAssignableTo<JsonException>();
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
        await Assert.That(current.SchemaStale).IsFalse();

        await Assert.ThrowsExactlyAsync<JsonException>(() =>
            current.Source<PreNullableEmployee>("Employee", ["Name", "ManagerId"]).ToListAsync());
    }

    [Test]
    public async Task DisallowedPropertyThrowsThroughClient() =>
        // The generated client model has no Salary member (the server marks it [QueryIgnore]), so
        // attempts to reach hidden data must come as raw requests, which the server rejects (see the
        // 400 test). Here we confirm an unknown root is rejected through the typed client path.
        await Assert.ThrowsExactlyAsync<ScryRequestException>(() =>
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

        await Assert.That((await employees).Select(_ => _.Name)).IsEquivalentTo(activeEmployeeNames, CollectionOrdering.Matching);
        await Assert.That(await departments).IsEqualTo(2);
        await Assert.That((await first)!.Name).IsEqualTo("Aaron");
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

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(async () => await rejected))!;
        await Assert.That(exception.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await accepted).IsEqualTo(2);
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

        await Assert.That(rows).IsNotEmpty();
        await Assert.That(echoed).IsEqualTo("round-trip-1");
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

        await Assert.That(regions).IsNotEmpty();
        await Assert.That(echoed).IsEqualTo("round-trip-2");
    }

    // A rejected query still has response headers, and they are the ones worth reading.
    [Test]
    public async Task ResponseHeadersAreReadableOnARejectedQueryOverHttp()
    {
        string? stamp = null;

        var rejected = client.Source<EmployeeQueryModel>("Secret")
            .OnResponseHeaders(_ => stamp = _.GetValues(WireFormat.SchemaStampHeader).Single());

        await Assert.ThrowsExactlyAsync<ScryRequestException>(() => rejected.ToListAsync());
        await Assert.That(stamp).IsEqualTo(ScryQuery.SchemaStamp);
    }

    record RegionRow(string Region);

    /// <summary>
    /// What a client generated before the member was marked does: it reads its own model, sees nothing
    /// sensitive, and asks in a URL. The refusal is one it can act on without a person reading it, so
    /// the query still returns — one round trip later, in a body — rather than failing.
    /// </summary>
    /// <remarks>
    /// First in the assembly, since what a client believes is sensitive is the union of every model any
    /// client in the process opened a source as: once anything here opens the generated Employee, which
    /// marks Password, no client in this process can be stale about it, and this asks in a body at once.
    /// A class of its own, over a server of its own, because every other test class depends on it and a
    /// class cannot depend on one of its own tests.
    /// </remarks>
    [NotInParallel]
    public class StaleClient
    {
        [Before(Class)]
        public static Task StartServer() =>
            HttpRoundTripTests.StartServer();

        [After(Class)]
        public static Task StopServer() =>
            HttpRoundTripTests.StopServer();

        [Test]
        public async Task AClientThatDoesNotKnowRetriesInABody()
        {
            var stale = ScryClient.ForHttp(http, "/api/query");
            methods.Clear();

            var rows = await stale.Source<UnmarkedEmployee>("Employee", ["Id", "Name", "Password"])
                .Where(_ => _.Password == "hunter2")
                .Select(_ => new NameRow(_.Name))
                .ToListAsync();

            // Asked the way it believed it could, refused, and asked again the way it was told to —
            // which is the whole of the self-healing, and is why this is two requests rather than one.
            await Assert.That(methods).IsEquivalentTo(["GET", "POST"], CollectionOrdering.Matching);
            await Assert.That(rows).IsEmpty();
        }
    }
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

/// <summary>
/// The same for Employee.Photo. A type of its own rather than a second interface on the one above,
/// which the server refuses: one policy authorizing two entities leaves it ambiguous which rows it
/// was answering about.
/// </summary>
public sealed class AllowPhotoAttachmentPolicy :
    IAttachmentPolicy<Sample.Model.Employee>
{
    public bool Authorize(ScryAttachmentContext context) => true;
}
