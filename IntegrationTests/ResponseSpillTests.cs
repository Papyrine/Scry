// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// What happens when a response outgrows the buffer it was being held in. A result that fits is still
/// answered exactly as it always was — whole, with a declared length, and with a failure part-way
/// through it still a 500 carrying a body. One that does not gives up the length and the error, but
/// never a byte and never the ability to tell a truncated answer from a complete one.
/// </summary>
/// <remarks>
/// Every source here is a <c>[QueryablePoco]</c>, so nothing touches a database: the rows are supplied
/// by the fixture, which is also the only way to have a read that fails part-way on demand.
/// </remarks>
[TestFixture]
public class ResponseSpillTests
{
    // Wide rows, so a few hundred of them clear the default threshold, and distinctive ones, so a
    // truncated body is obvious rather than plausible.
    static IEnumerable<Sample.Model.Holiday> Wide(int count) =>
        Enumerable
            .Range(0, count)
            .Select(index => new Sample.Model.Holiday
            {
                Name = new('w', 200),
                Date = new DateOnly(2030, 1, 1).AddDays(index)
            });

    // The read that dies part-way through being written out: rows arrive, then the source throws.
    static IEnumerable<Sample.Model.Holiday> Exploding(int before)
    {
        foreach (var holiday in Wide(before))
        {
            yield return holiday;
        }

        throw new InvalidOperationException("the read failed part-way");
    }

    const string listRequest =
        """
        {"version":1,"root":"Holiday","pipeline":[
          {"$type":"select","projection":{"members":[
            "Name",
            "Date"]}}]}
        """;

    /// <summary>
    /// The identity the golden corpus pins, asked of a response that stopped being buffered part-way:
    /// draining mid-array must change nothing about the bytes. Twice, because the first send builds the
    /// plan and the second replays it.
    /// </summary>
    [Test]
    public async Task SpilledBytesMatchTheGeneralPath()
    {
        await using var app = await Start(64 * 1024, () => Wide(600));
        using var http = app.GetTestClient();
        var expected = Direct(app, listRequest);

        Assert.That(expected.Length, Is.GreaterThan(64 * 1024), "the corpus must actually spill");
        Assert.That(await Post(http, listRequest), Is.EqualTo(expected), "miss");
        Assert.That(await Post(http, listRequest), Is.EqualTo(expected), "hit");
    }

    /// <summary>
    /// The same identity for a batch, which drains between entries but never inside one. Nothing in
    /// this model carries <c>[BinaryTransfer]</c>, which is the only condition under which a batch may
    /// drain at all — its parts would be numbered globally and its envelope arrives last.
    /// </summary>
    [Test]
    public async Task SpilledBatchBytesMatchTheGeneralPath()
    {
        await using var app = await Start(64 * 1024, () => Wide(400));
        using var http = app.GetTestClient();
        var batch = $$"""{"version":1,"queries":[{{listRequest}},{{listRequest}}]}""";
        var expected = DirectBatch(app, batch);

        Assert.That(expected.Length, Is.GreaterThan(64 * 1024), "the batch must actually spill");
        Assert.That(await Post(http, batch, "/api/query/batch"), Is.EqualTo(expected), "miss");
        Assert.That(await Post(http, batch, "/api/query/batch"), Is.EqualTo(expected), "hit");
    }

    // Nothing went out early, so the pending bytes are the whole body and can say how many they are.
    [Test]
    public async Task AResponseThatFitsDeclaresItsLength()
    {
        await using var app = await Start(64 * 1024, () => Wide(3));
        using var http = app.GetTestClient();

        // Headers-first: buffering the content lets the client compute a length of its own, which would
        // report the same number whether or not the response ever declared one.
        using var response = await Send(http, listRequest, HttpCompletionOption.ResponseHeadersRead);
        var declared = response.Content.Headers.ContentLength;
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Multiple(() =>
        {
            Assert.That(declared, Is.EqualTo(body.Length));
            Assert.That(response.Headers.TransferEncodingChunked, Is.Not.True);
        });
    }

    // A length can only describe a whole body, and the first drain is the moment this stops having one.
    [Test]
    public async Task ASpilledResponseDeclaresNoLength()
    {
        await using var app = await Start(64 * 1024, () => Wide(600));
        using var http = app.GetTestClient();

        using var response = await Send(http, listRequest, HttpCompletionOption.ResponseHeadersRead);
        var declared = response.Content.Headers.ContentLength;
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(declared, Is.Null);
            // Given up the length, not a byte of the answer.
            Assert.That(body, Does.EndWith("}"));
            Assert.That(body.Length, Is.GreaterThan(64 * 1024));
        });
    }

    /// <summary>
    /// Past the threshold the status is long since sent, so a failure has no way to become a 500 — the
    /// answer is truncated instead. What must hold is that a truncated one is never mistakable for a
    /// complete one: the closing stamp is written only after the last row, so its absence is the tell,
    /// and the host may equally tear the connection down before the body is even readable.
    /// </summary>
    [Test]
    public async Task AFailurePastTheWatermarkTruncatesTheResponse()
    {
        await using var app = await Start(1024, () => Exploding(200));
        using var http = app.GetTestClient();

        using var response = await Send(http, listRequest, HttpCompletionOption.ResponseHeadersRead);

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception)
        {
            // The other way a truncation presents: the body ended before the host said it would.
        }

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                body is null || !body.Contains("\"stamp\""),
                Is.True,
                $"a truncated response must not look complete, but was: {body}");
        });
    }

    /// <summary>
    /// The other side of the same coin, and the one that pins that the threshold did not quietly turn
    /// every failure into a truncation: a result that never reached it is still answered as an error
    /// with a body, exactly as every result was before spilling existed.
    /// </summary>
    [Test]
    public async Task AFailureBeforeTheWatermarkIsStillAnError()
    {
        await using var app = await Start(64 * 1024, () => Exploding(2));
        using var http = app.GetTestClient();

        using var response = await Send(http, listRequest);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(body, Does.Contain("Query execution failed."));
        });
    }

    // Zero holds every response whole, which is what every response was before there was a threshold.
    [Test]
    public async Task HoldsEveryResponseWholeWhenTheThresholdIsZero()
    {
        await using var app = await Start(0, () => Wide(600));
        using var http = app.GetTestClient();

        using var response = await Send(http, listRequest);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(body.Length));
    }

    static async Task<string> Post(HttpClient http, string request, string path = "/api/query")
    {
        using var response = await Send(http, request, path: path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
        return body;
    }

    static Task<HttpResponseMessage> Send(
        HttpClient http,
        string request,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        string path = "/api/query")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(request, Encoding.UTF8, "application/json")
        };
        return http.SendAsync(message, completion);
    }

    // The general path: the same request through ScryProcessor.Execute, which buffers whatever the
    // threshold says and so is the fixed point the written bytes are compared against.
    static string Direct(WebApplication app, string request)
    {
        var parsed = ScryJson.DeserializeRequest(request);
        var processor = app.Services.GetRequiredService<ScryProcessor>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Sample.Model.SampleContext>();
        return ScryJson.Serialize(processor.Execute(parsed, db, scope.ServiceProvider));
    }

    // The general path for a batch: a JsonElement per entry, then one reflection pass over the envelope
    // that serializes every one of them a second time.
    static string DirectBatch(WebApplication app, string request)
    {
        var parsed = ScryJson.DeserializeBatchRequest(request);
        var processor = app.Services.GetRequiredService<ScryProcessor>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Sample.Model.SampleContext>();
        return ScryJson.Serialize(processor.ExecuteBatch(parsed, db, scope.ServiceProvider));
    }

    // A server per test, because the threshold is fixed at startup and so is the source that fails.
    // The connection string is never opened: every query here reads the poco source.
    static async Task<WebApplication> Start(int threshold, Func<IEnumerable<Sample.Model.Holiday>> rows)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(
            _ => _.UseSqlServer("Server=(localdb)\\ScryUnused;Database=ScryUnused"));
        builder.Services.AddScry<Sample.Model.SampleContext>(options =>
        {
            options.ResponseSpillThreshold = threshold;
            options.AddPocoSource(_ => rows());
            // Department.Handbook is an [Attachment], and startup refuses a source whose attachment
            // nothing authorizes. No test here fetches it, so an allow-all satisfies the check.
            options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
        });

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }
}
