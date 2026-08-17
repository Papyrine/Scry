// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// The fast response writer's whole contract is byte identity: what the HTTP endpoint streams from
/// projected rows must equal serializing the <see cref="QueryResponse"/> the general path produces —
/// for every result kind, on the plan-cache miss and on the hit, and for strings that stress every
/// escaping path. A mismatch here is a wire break, not a style difference. The sample model has no
/// binary member, so this corpus never diverts; the multipart counterpart of this identity is
/// <c>BinaryTransferTests.FastAndGeneralPathsEmitIdenticalPayloads</c>.
/// </summary>
[TestFixture]
public class FastWriterGoldenTests
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
    SqlDatabase<Sample.Model.SampleContext> database = null!;

    // Every JSON escaping and encoding path in one list: quotes, backslashes, control characters,
    // multi-codepoint emoji, right-to-left marks, HTML-sensitive text, and a SQL-looking string.
    static readonly string[] naughty =
    [
        """quote " and \ backslash""",
        "line\nbreak\ttab\rreturn",
        "\u0001control\u001Fchars",
        "emoji 👨‍👩‍👧‍👦 and 𝔘𝔫𝔦𝔠𝔬𝔡𝔢",
        "rtl ‏mark and <script>&amp;</script>",
        "'; DROP TABLE Employees;--"
    ];

    [OneTimeSetUp]
    public async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<Sample.Model.SampleContext>(options =>
        {
            options.AddPocoSource(_ => naughty.Select((text, index) => new Sample.Model.Holiday
            {
                Name = text,
                Date = new(2026, 1, index + 1)
            }));
            // Department.Handbook is an [Attachment], and startup refuses a source whose attachment
            // nothing authorizes. No test here fetches it, so an allow-all satisfies the check.
            options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
        });

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        await database.DisposeAsync();
    }

    public static IEnumerable<TestCaseData> Corpus()
    {
        yield return new(
            "nested projection with nulls",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"where","predicate":{"$type":"member","path":"Active"}},
              {"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}},
                {"name":"Status","value":{"$type":"node","node":{"$type":"member","path":"Status"}}},
                {"name":"Created","value":{"$type":"node","node":{"$type":"member","path":"Created"}}},
                {"name":"ManagerId","value":{"$type":"node","node":{"$type":"member","path":"ManagerId"}}},
                {"name":"Manager","value":{"$type":"node","node":{"$type":"member","path":["Manager","Name"]}}},
                {"name":"Department","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}}]}
            """);
        yield return new(
            "naughty strings through a poco source",
            """
            {"version":1,"root":"Holiday","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Date"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}},
                {"name":"Date","value":{"$type":"node","node":{"$type":"member","path":"Date"}}}]}}]}
            """);
        yield return new(
            "duplicate projected name overwrites in place",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Id"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}},
                {"name":"Active","value":{"$type":"node","node":{"$type":"member","path":"Active"}}},
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}}]}
            """);
        yield return new(
            "nested path claiming a scalar's position",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Id"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Boss","value":{"$type":"node","node":{"$type":"member","path":"Name"}}},
                {"name":"Boss","value":{"$type":"nested","path":"Manager","projection":{"members":[
                  {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}}]}}}]}}]}
            """);
        yield return new(
            "default projection",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Id"},"descending":false}]}
            """);
        yield return new(
            "scalar terminal",
            """
            {"version":1,"root":"Employee","pipeline":[{"$type":"count"}]}
            """);
        // A count is an int and an aggregate is whatever the provider returns for it, which covers both
        // halves of the value writer: a decimal is one of its fast cases, and a date-without-time is
        // handed to the serializer.
        yield return new(
            "aggregate scalar terminal",
            """
            {"version":1,"root":"Order","pipeline":[
              {"$type":"aggregate","function":"Sum","selector":{"$type":"member","path":"Amount"}}]}
            """);
        yield return new(
            "aggregate scalar over a date",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"aggregate","function":"Max","selector":{"$type":"member","path":"Created"}}]}
            """);
        yield return new(
            "all terminal",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"all","predicate":{"$type":"member","path":"Active"}}]}
            """);
        yield return new(
            "single row terminal",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},
              {"$type":"first","orDefault":false,"predicate":null}]}
            """);
        yield return new(
            "null single terminal",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"where","predicate":{"$type":"binary","op":"Equal",
                "left":{"$type":"member","path":"Name"},
                "right":{"$type":"const","value":"Nobody","tag":"String"}}},
              {"$type":"first","orDefault":true,"predicate":null}]}
            """);
        yield return new(
            "page envelope",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}}]}},
              {"$type":"page","size":2}]}
            """);
        // The page above has a further page and so mints a cursor. This one asks for more rows than
        // exist, so there is nothing to resume from and the cursor is omitted rather than written as
        // null — the writer's other branch, and a different set of bytes.
        yield return new(
            "page envelope with no further page",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}}]}},
              {"$type":"page","size":100}]}
            """);
        // A poco source is never seek-safe, so a page of one carries no cursor even when a further
        // page exists — the same omission arrived at down a different path.
        yield return new(
            "page envelope over a poco source",
            """
            {"version":1,"root":"Holiday","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Date"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}},
                {"name":"Date","value":{"$type":"node","node":{"$type":"member","path":"Date"}}}]}},
              {"$type":"page","size":2}]}
            """);
        yield return new(
            "grouped aggregates",
            """
            {"version":1,"root":"Order","pipeline":[
              {"$type":"groupBy","keys":[{"$type":"member","path":"Region"}]},
              {"$type":"select","projection":{"members":[
                {"name":"Region","value":{"$type":"node","node":{"$type":"member","path":"Region"}}},
                {"name":"Total","value":{"$type":"node","node":{"$type":"aggregate","function":"Sum","selector":{"$type":"member","path":"Amount"}}}},
                {"name":"Count","value":{"$type":"node","node":{"$type":"aggregate","function":"Count"}}}]}}]}
            """);
        yield return new(
            "distinct projection",
            """
            {"version":1,"root":"Employee","pipeline":[
              {"$type":"select","projection":{"members":[
                {"name":"Department","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}},
              {"$type":"distinct"}]}
            """);
    }

    [TestCaseSource(nameof(Corpus))]
    public async Task FastBytesMatchTheGeneralPath(string name, string request)
    {
        var expected = Direct(request);

        // Twice: the first send builds and caches the plan, the second replays it — the writer must
        // be byte-identical on both.
        Assert.That(await Post(request), Is.EqualTo(expected), $"{name} (miss)");
        Assert.That(await Post(request), Is.EqualTo(expected), $"{name} (hit)");
    }

    [Test]
    public async Task StreamBytesMatchTheGeneralPath()
    {
        const string request =
            """
            {"version":1,"root":"Holiday","pipeline":[
              {"$type":"orderBy","key":{"$type":"member","path":"Date"},"descending":false},
              {"$type":"select","projection":{"members":[
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}},
                {"name":"Date","value":{"$type":"node","node":{"$type":"member","path":"Date"}}}]}}]}
            """;
        var expected = await DirectStream(request);

        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query/stream", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(body, Is.EqualTo(expected));
    }

    /// <summary>
    /// A batch envelope is written the same way a single response is, so the same identity has to hold
    /// for it. Every corpus request in one batch — which puts the two entry shapes the row writer does
    /// not produce, a terminal's response and a page, beside the lists — plus a rejected entry and one
    /// whose rejection is attributed to a drifted stamp, so the <c>error</c>, <c>status</c> and
    /// <c>staleClient</c> members are written too.
    /// </summary>
    [Test]
    public async Task BatchBytesMatchTheGeneralPath()
    {
        var queries = Corpus()
            .Select(_ => (string)_.Arguments[1]!)
            .ToList();
        queries.Add("""{"version":1,"root":"Nonexistent","pipeline":[]}""");
        queries.Add("""{"version":1,"root":"Nonexistent","pipeline":[],"stamp":"not-this-server"}""");

        var batch = $$"""{"version":1,"queries":[{{string.Join(",", queries)}}]}""";
        var expected = DirectBatch(batch);

        Assert.That(await Post(batch, "/api/query/batch"), Is.EqualTo(expected), "miss");
        Assert.That(await Post(batch, "/api/query/batch"), Is.EqualTo(expected), "hit");
    }

    async Task<string> Post(string request, string path = "/api/query")
    {
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
        return body;
    }

    // The general path: the same request through ScryProcessor.Execute — dictionaries, JsonElement,
    // full reflection serialization — rendered exactly as the endpoint would render it.
    string Direct(string request)
    {
        var parsed = ScryJson.DeserializeRequest(request);
        var processor = app.Services.GetRequiredService<ScryProcessor>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Sample.Model.SampleContext>();
        return ScryJson.Serialize(processor.Execute(parsed, db, scope.ServiceProvider));
    }

    // The general path for a batch: dictionaries and a JsonElement per entry, then one reflection pass
    // over the envelope that serializes every one of them a second time.
    string DirectBatch(string request)
    {
        var parsed = ScryJson.DeserializeBatchRequest(request);
        var processor = app.Services.GetRequiredService<ScryProcessor>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Sample.Model.SampleContext>();
        return ScryJson.Serialize(processor.ExecuteBatch(parsed, db, scope.ServiceProvider));
    }

    async Task<string> DirectStream(string request)
    {
        var parsed = ScryJson.DeserializeRequest(request);
        var processor = app.Services.GetRequiredService<ScryProcessor>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Sample.Model.SampleContext>();

        var (begin, rows) = processor.Stream(parsed, db, scope.ServiceProvider);
        var text = new StringBuilder();
        text.Append(ScryJson.Serialize(begin)).Append('\n');
        await foreach (var row in rows)
        {
            text.Append(JsonSerializer.Serialize(row, ScryJson.Options)).Append('\n');
        }

        text.Append(ScryJson.Serialize(new ScryStreamMarker { Kind = ScryStream.End })).Append('\n');
        return text.ToString();
    }
}
