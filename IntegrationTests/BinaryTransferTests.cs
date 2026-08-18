// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

// ReSharper disable NotAccessedPositionalProperty.Local

/// <summary>
/// The [BinaryTransfer] contract over HTTP, end to end: a byte[] member's values travel as raw
/// multipart parts on all three endpoints, parts precede the JSON that references them, indices are
/// per-document (per row line on the stream, global across a batch), null stays inline, and a
/// binary-free result is plain JSON exactly as before. On a stream the framing is the plan's decision
/// rather than the data's, and a failure part-way through still ends in the stream's error marker with
/// the parts already sent left intact. The fixture is self-contained — the sample model has no binary
/// member — with its own context, schema, and server.
/// </summary>
[TestFixture]
public class BinaryTransferTests
{
    static readonly byte[] alphaPayload = [0x01, 0x02, 0x03];

    // Boundary-shaped content: the delimiter prefix in the part bytes proves the random boundary is
    // never confused by content, and the 0x00/0xFF spread catches any text-mode mangling.
    static readonly byte[] boundaryPayload = [.."\r\n--scry"u8.ToArray(), 0x00, 0xFF, 0x0D, 0x0A];

    static readonly byte[] emptyPayload = [];

    static readonly byte[] fullPayload = [..Enumerable.Range(0, 256).Select(_ => (byte)_)];

    static readonly SqlInstance<BinaryContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: async context =>
        {
            await context.Database.EnsureCreatedAsync();
            context.Documents.AddRange(
                new() {Name = "alpha", Payload = alphaPayload, Kind = DocumentKind.Draft},
                new() {Name = "boundary", Payload = boundaryPayload, Kind = DocumentKind.Final},
                new() {Name = "empty", Payload = emptyPayload, Kind = DocumentKind.Draft},
                new() {Name = "missing", Payload = null, Kind = DocumentKind.Draft},
                new() {Name = "full", Payload = fullPayload, Kind = DocumentKind.Final});
            await context.SaveChangesAsync();
        });

    WebApplication app = null!;
    HttpClient http = null!;
    ScryClient client = null!;
    SqlDatabase<BinaryContext> database = null!;

    record Doc(int Id, string Name, byte[]? Payload, DocumentKind Kind);

    static readonly string[] docMembers = ["Id", "Name", "Payload", "Kind"];

    IQueryable<Doc> Documents =>
        client.Source<Doc>("Document", docMembers);

    [OneTimeSetUp]
    public async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<BinaryContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<BinaryContext>(_ => { });

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        client = ScryClient.ForHttp(http, "/api/query");
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        await database.DisposeAsync();
    }

    static readonly (string Name, byte[]? Payload)[] seeded =
    [
        ("alpha", alphaPayload),
        ("boundary", boundaryPayload),
        ("empty", emptyPayload),
        ("missing", null),
        ("full", fullPayload)
    ];

    [Test]
    public async Task ListRoundTripsBinaryOverMultipart()
    {
        var rows = await Documents
            .OrderBy(_ => _.Id)
            .ToListAsync();

        Assert.That(rows.Select(_ => (_.Name, _.Payload)), Is.EqualTo(seeded));
    }

    // The projection the framing tests share: a select with a [BinaryTransfer] slot in it, which is
    // what commits a response to multipart.
    const string selectNameAndPayload =
        """
        {"$type":"select","projection":{"members":[
          "Name",
          "Payload"]}}
        """;

    const string orderById =
        """{"$type":"orderBy","key":{"$type":"member","path":"Id"},"descending":false}""";

    const string listRequest =
        $$"""{"version":1,"root":"Document","pipeline":[{{orderById}},{{selectNameAndPayload}}]}""";

    // The stamp a client generated against a different model surface sends.
    const string driftStamp = "not-the-server's-stamp";

    const string driftedRequest =
        $$"""{"version":1,"stamp":"{{driftStamp}}","root":"Document","pipeline":[{{orderById}},{{selectNameAndPayload}}]}""";

    // The same projection narrowed to one name, so a stream can be pointed at a row that carries no
    // bytes — or at no rows at all. Four '$' because the predicate closes three braces in a row, which
    // fewer would read as an interpolation.
    static string NamedRequest(string name) =>
        $$$$"""
            {"version":1,"root":"Document","pipeline":[
              {"$type":"where","predicate":{"$type":"binary","op":"Equal",
                "left":{"$type":"member","path":"Name"},
                "right":{"$type":"const","value":"{{{{name}}}}","tag":"String"}}},
              {{{{selectNameAndPayload}}}}]}
            """;

    [Test]
    public async Task ResponseCarriesPartsBeforeTheEnvelope()
    {
        var (contentType, body) = await PostRaw("/api/query", listRequest);
        var boundary = BoundaryOf(contentType);
        var sections = ParseMultipart(body, boundary);

        // Four non-null payloads → four parts, in row order, each byte-exact — then the envelope.
        Assert.That(sections, Has.Count.EqualTo(5));
        Assert.That(sections[..4].Select(_ => _.Headers["Content-Type"]), Is.All.EqualTo(ScryBinary.PartContentType));
        Assert.That(sections[0].Content, Is.EqualTo(alphaPayload));
        Assert.That(sections[1].Content, Is.EqualTo(boundaryPayload));
        Assert.That(sections[2].Content, Is.Empty);
        Assert.That(sections[3].Content, Is.EqualTo(fullPayload));
        Assert.That(sections[..4].Select(_ => int.Parse(_.Headers["Content-Length"])), Is.EqualTo([3, boundaryPayload.Length, 0, 256]));

        Assert.That(sections[4].Headers["Content-Type"], Is.EqualTo("application/json"));
        var envelope = Encoding.UTF8.GetString(sections[4].Content);
        // Placeholders number the parts in emission order; a null value stays inline and takes no index.
        Assert.That(envelope, Does.Contain("""{"name":"alpha","payload":{"$bin":0}}"""));
        Assert.That(envelope, Does.Contain("""{"name":"boundary","payload":{"$bin":1}}"""));
        Assert.That(envelope, Does.Contain("""{"name":"empty","payload":{"$bin":2}}"""));
        Assert.That(envelope, Does.Contain("""{"name":"missing","payload":null}"""));
        Assert.That(envelope, Does.Contain("""{"name":"full","payload":{"$bin":3}}"""));
    }

    [Test]
    public async Task BinaryFreeResultsStayPlainOnEveryEndpoint()
    {
        const string namesOnly =
            """
            {"version":1,"root":"Document","pipeline":[
              {"$type":"select","projection":{"members":[
                "Name"]}}]}
            """;

        var (single, _) = await PostRaw("/api/query", namesOnly);
        Assert.That(single, Does.StartWith("application/json"));

        var (stream, _) = await PostRaw("/api/query/stream", namesOnly);
        Assert.That(stream, Does.StartWith(ScryStream.ContentType));

        var (batch, _) = await PostRaw("/api/query/batch", $$"""{"version":1,"queries":[{{namesOnly}}]}""");
        Assert.That(batch, Does.StartWith("application/json"));
    }

    /// <summary>
    /// A diverting result is held whole however small the buffer it is allowed, because its parts have
    /// to precede the JSON that references them and a drained envelope could not be preceded by
    /// anything. The threshold here is one byte, so every other result on this server spills.
    /// </summary>
    [Test]
    public async Task ADivertingResultIsHeldWholeHoweverLowTheThreshold()
    {
        await using var spilling = await StartSpilling(1);
        using var spillingHttp = spilling.GetTestClient();

        var (contentType, body) = await PostRaw(spillingHttp, "/api/query", listRequest);
        var sections = ParseMultipart(body, BoundaryOf(contentType));

        // The framing this fixture already pins, arrived at with spilling switched on as hard as it goes.
        Assert.That(sections, Has.Count.EqualTo(5));
        Assert.That(sections[0].Content, Is.EqualTo(alphaPayload));
        Assert.That(sections[3].Content, Is.EqualTo(fullPayload));
        Assert.That(sections[4].Headers["Content-Type"], Is.EqualTo("application/json"));
        Assert.That(
            Encoding.UTF8.GetString(sections[4].Content),
            Does.Contain("""{"name":"alpha","payload":{"$bin":0}}"""));
    }

    /// <summary>
    /// The gate is the projection plan, not the schema: the same source, projected without its binary
    /// member, carries no slot that could divert and so is free to spill. A response that spilled
    /// declares no length, which is how one tells it did.
    /// </summary>
    [Test]
    public async Task ABinaryFreeProjectionOverABinarySourceStillSpills()
    {
        const string namesOnly =
            """
            {"version":1,"root":"Document","pipeline":[
              {"$type":"select","projection":{"members":[
                "Name"]}}]}
            """;

        await using var spilling = await StartSpilling(1);
        using var spillingHttp = spilling.GetTestClient();

        using var content = new StringContent(namesOnly, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/query") {Content = content};
        // Headers-first: buffering the content lets the client compute a length the response never sent.
        using var response = await spillingHttp.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
        var declared = response.Content.Headers.ContentLength;
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.Content.Headers.ContentType!.ToString(), Does.StartWith("application/json"));
            Assert.That(declared, Is.Null);
            Assert.That(body, Does.Contain("""{"name":"alpha"}"""));
            Assert.That(body, Does.EndWith("}"));
        });
    }

    /// <summary>
    /// A batch cannot ask the plan's question, because it commits to one framing before the first entry
    /// runs and only entry n's plan says whether entry n diverts. So it asks the model's instead, and
    /// this model has a binary member — which holds the whole batch whole even for entries that could
    /// not possibly divert, and even with the threshold at one byte.
    /// </summary>
    [Test]
    public async Task ABatchOnABinaryCarryingModelIsHeldWhole()
    {
        const string namesOnly =
            """
            {"version":1,"root":"Document","pipeline":[
              {"$type":"select","projection":{"members":[
                "Name"]}}]}
            """;

        await using var spilling = await StartSpilling(1);
        using var spillingHttp = spilling.GetTestClient();

        var batch = $$"""{"version":1,"queries":[{{namesOnly}},{{namesOnly}}]}""";
        using var content = new StringContent(batch, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/query/batch") {Content = content};
        using var response = await spillingHttp.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
        var declared = response.Content.Headers.ContentLength;
        var body = await response.Content.ReadAsByteArrayAsync();

        // A declared length is what never having drained looks like from the outside.
        Assert.That(declared, Is.EqualTo(body.Length));
    }

    [Test]
    public async Task SingleTerminalRoundTripsBinary()
    {
        var row = await Documents
            .Where(_ => _.Name == "full")
            .FirstAsync();

        Assert.That(row!.Payload, Is.EqualTo(fullPayload));
    }

    [Test]
    public async Task PageTerminalRoundTripsBinary()
    {
        var page = await Documents
            .OrderBy(_ => _.Id)
            .ToPageAsync(3);

        Assert.That(page.Items.Select(_ => (_.Name, _.Payload)), Is.EqualTo(seeded[..3]));
    }

    [Test]
    public async Task DriftedClientStillRoundTripsBinary()
    {
        // A mismatched stamp with enum aliases in the schema (DocumentKind carries a renamed value)
        // pushes the server onto the fully-general fallback path — which must divert identically.
        var drifted = ScryClient.ForHttp(http, "/api/query");
        drifted.SchemaStamp = driftStamp;

        var rows = await drifted.Source<Doc>("Document", docMembers)
            .OrderBy(_ => _.Id)
            .ToListAsync();

        Assert.That(rows.Select(_ => (_.Name, _.Payload)), Is.EqualTo(seeded));
    }

    [Test]
    public async Task StreamRoundTripsBinaryPerRow()
    {
        var rows = new List<Doc>();
        await foreach (var row in Documents.OrderBy(_ => _.Id).ToAsyncEnumerable())
        {
            rows.Add(row);
        }

        Assert.That(rows.Select(_ => (_.Name, _.Payload)), Is.EqualTo(seeded));
    }

    [Test]
    public async Task StreamAlternatesSectionsAndResetsIndicesPerRow()
    {
        var (contentType, body) = await PostRaw("/api/query/stream", listRequest);
        Assert.That(contentType, Does.StartWith(ScryBinary.ContentType));
        var sections = ParseMultipart(body, BoundaryOf(contentType));

        // Ndjson sections alternate with each row's parts: begin | part | alpha-row | part |
        // boundary-row | part | empty-row + partless missing-row | part | full-row + end.
        Assert.That(
            sections.Select(_ => _.Headers["Content-Type"]),
            Is.EqualTo([
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType
            ]));

        Assert.That(sections[1].Content, Is.EqualTo(alphaPayload));
        Assert.That(sections[3].Content, Is.EqualTo(boundaryPayload));
        Assert.That(sections[5].Content, Is.Empty);
        Assert.That(sections[7].Content, Is.EqualTo(fullPayload));

        var lines = sections
            .Where(_ => _.Headers["Content-Type"] == ScryStream.ContentType)
            .Select(_ => Encoding.UTF8.GetString(_.Content))
            .ToArray();
        Assert.That(lines[0], Does.Contain(ScryStream.MarkerProperty).And.Contain(ScryStream.Begin));
        // Every row's placeholder is index 0 again: a stream's indices reset per row line.
        Assert.That(lines[1], Does.Contain("""{"name":"alpha","payload":{"$bin":0}}"""));
        Assert.That(lines[2], Does.Contain("""{"name":"boundary","payload":{"$bin":0}}"""));
        // The partless row rides the same section as the row before it.
        Assert.That(lines[3], Does.Contain("""{"name":"empty","payload":{"$bin":0}}"""));
        Assert.That(lines[3], Does.Contain("""{"name":"missing","payload":null}"""));
        Assert.That(lines[4], Does.Contain("""{"name":"full","payload":{"$bin":0}}"""));
        Assert.That(lines[4], Does.Contain(ScryStream.End));
    }

    [Test]
    public async Task StreamCommitsToMultipartBeforeTheFirstRow()
    {
        // The framing is the plan's decision, not the data's: the content type is fixed before a row
        // is pulled, so a projection with a binary slot wraps even when nothing ends up diverting.
        // A single null-valued row, and no rows at all, are the two ways that can happen.
        var (nullType, nullBody) = await PostRaw("/api/query/stream", NamedRequest("missing"));
        var nullSections = ParseMultipart(nullBody, BoundaryOf(nullType));

        Assert.That(nullSections.Select(_ => _.Headers["Content-Type"]), Is.EqualTo([ScryStream.ContentType]));
        var nullLines = LinesOf(nullSections[0]);
        Assert.That(nullLines, Has.Length.EqualTo(3));
        Assert.That(nullLines[1], Does.Contain("""{"name":"missing","payload":null}"""));

        var (emptyType, emptyBody) = await PostRaw("/api/query/stream", NamedRequest("nothing is named this"));
        var emptySections = ParseMultipart(emptyBody, BoundaryOf(emptyType));

        Assert.That(emptySections.Select(_ => _.Headers["Content-Type"]), Is.EqualTo([ScryStream.ContentType]));
        // Nothing between the markers: an empty result is still a multipart response, just an empty one.
        var emptyLines = LinesOf(emptySections[0]);
        Assert.That(emptyLines, Has.Length.EqualTo(2));
        Assert.That(emptyLines[0], Does.Contain(ScryStream.Begin));
        Assert.That(emptyLines[1], Does.Contain(ScryStream.End));
    }

    [Test]
    public async Task DriftedClientStillStreamsBinary()
    {
        var drifted = ScryClient.ForHttp(http, "/api/query");
        drifted.SchemaStamp = driftStamp;
        var documents = drifted.Source<Doc>("Document", docMembers);

        var rows = new List<Doc>();
        await foreach (var row in documents.OrderBy(_ => _.Id).ToAsyncEnumerable())
        {
            rows.Add(row);
        }

        Assert.That(rows.Select(_ => (_.Name, _.Payload)), Is.EqualTo(seeded));
    }

    [Test]
    public async Task DriftedStreamCarriesTheAliasesAndKeepsItsParts()
    {
        var (contentType, body) = await PostRaw("/api/query/stream", driftedRequest);
        var sections = ParseMultipart(body, BoundaryOf(contentType));

        // A mismatched stamp adds the enum alias table to the begin marker, which is the one thing on
        // this path that differs. Everything around it is the framing a matching client gets: the same
        // alternating sections, the same bytes.
        Assert.That(sections, Has.Count.EqualTo(9));
        Assert.That(LinesOf(sections[0])[0], Does.Contain("DocumentKind").And.Contain("Sketch"));
        Assert.That(sections[1].Content, Is.EqualTo(alphaPayload));
        Assert.That(sections[3].Content, Is.EqualTo(boundaryPayload));
        Assert.That(sections[5].Content, Is.Empty);
        Assert.That(sections[7].Content, Is.EqualTo(fullPayload));
    }

    [Test]
    public async Task StreamOverTheRowLimitFailsAfterThePartsAlreadySent()
    {
        await using var limited = await StartLimited(maxStreamRows: 2);
        using var limitedHttp = limited.GetTestClient();

        var (contentType, body) = await PostRaw(limitedHttp, "/api/query/stream", listRequest);
        var sections = ParseMultipart(body, BoundaryOf(contentType));

        // Two rows and their parts are on the wire by the time the limit trips, and the status is long
        // since sent — so the failure rides the stream's error marker, in the section the last row was
        // written into, and the closing marker never comes.
        Assert.That(
            sections.Select(_ => _.Headers["Content-Type"]),
            Is.EqualTo([
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType
            ]));
        Assert.That(sections[1].Content, Is.EqualTo(alphaPayload));
        Assert.That(sections[3].Content, Is.EqualTo(boundaryPayload));

        var last = LinesOf(sections[4]);
        Assert.That(last[0], Does.Contain("""{"name":"boundary","payload":{"$bin":0}}"""));
        Assert.That(last[1], Does.Contain($"\"{ScryStream.MarkerProperty}\":\"{ScryStream.Error}\""));
        Assert.That(last[1], Does.Contain("more than the maximum of 2 streamed rows"));
        Assert.That(
            Encoding.UTF8.GetString(body),
            Does.Not.Contain($"\"{ScryStream.MarkerProperty}\":\"{ScryStream.End}\""));
    }

    [Test]
    public async Task StreamOverTheRowLimitSurfacesTheFailureToTheReader()
    {
        await using var limited = await StartLimited(maxStreamRows: 2);
        using var limitedHttp = limited.GetTestClient();
        var limitedClient = ScryClient.ForHttp(limitedHttp, "/api/query");
        var documents = limitedClient.Source<Doc>("Document", docMembers);

        var rows = new List<Doc>();
        var exception = Assert.ThrowsAsync<ScryWireException>(
            async () =>
            {
                await foreach (var row in documents.OrderBy(_ => _.Id).ToAsyncEnumerable())
                {
                    rows.Add(row);
                }
            });

        Assert.That(exception!.Message, Does.Contain("more than the maximum of 2 streamed rows"));
        // The rows that did arrive are whole — their parts were read and resolved before the failure,
        // so a truncated stream is an error rather than a short answer with mangled bytes.
        Assert.That(rows.Select(_ => (_.Name, _.Payload)), Is.EqualTo(seeded[..2]));
    }

    // A second server, because the spill threshold is fixed at startup and the fixture's own server
    // keeps the default — under which nothing here is large enough to spill at all.
    async Task<WebApplication> StartSpilling(int threshold)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<BinaryContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<BinaryContext>(_ => _.ResponseSpillThreshold = threshold);

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }

    // A second server, because the row limit is fixed at startup and the fixture's own server has none.
    async Task<WebApplication> StartLimited(int maxStreamRows)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<BinaryContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<BinaryContext>(_ => _.MaxStreamRows = maxStreamRows);

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }

    [Test]
    public async Task BatchNumbersPartsGloballyAcrossEntries()
    {
        var batch = client.Batch();
        var withBinary = Documents
            .OrderBy(_ => _.Id)
            .Where(_ => _.Name == "alpha" || _.Name == "boundary")
            .InBatch(batch)
            .ToListAsync();
        var namesOnly = Documents
            .OrderBy(_ => _.Id)
            .Select(_ => new NameRow(_.Name))
            .InBatch(batch)
            .ToListAsync();
        var moreBinary = Documents
            .Where(_ => _.Name == "full")
            .InBatch(batch)
            .ToListAsync();

        await batch.SendAsync();

        Assert.That((await withBinary).Select(_ => (_.Name, _.Payload)), Is.EqualTo(seeded[..2]));
        Assert.That((await namesOnly).Select(_ => _.Name), Is.EqualTo(seeded.Select(_ => _.Name)));
        Assert.That((await moreBinary).Single().Payload, Is.EqualTo(fullPayload));
    }

    record NameRow(string Name);

    [Test]
    public async Task BatchEnvelopeNumbersPartsGlobally()
    {
        const string request =
            $$"""{"version":1,"queries":[{{listRequest}},{{listRequest}}]}""";

        var (contentType, body) = await PostRaw("/api/query/batch", request);
        Assert.That(contentType, Does.StartWith(ScryBinary.ContentType));
        var sections = ParseMultipart(body, BoundaryOf(contentType));

        // Two identical entries → eight parts (four each), one envelope, indices continuing across
        // the entry boundary rather than resetting.
        Assert.That(sections, Has.Count.EqualTo(9));
        var envelope = Encoding.UTF8.GetString(sections[8].Content);
        Assert.That(envelope, Does.Contain("""{"name":"alpha","payload":{"$bin":0}}"""));
        Assert.That(envelope, Does.Contain("""{"name":"full","payload":{"$bin":3}}"""));
        Assert.That(envelope, Does.Contain("""{"name":"alpha","payload":{"$bin":4}}"""));
        Assert.That(envelope, Does.Contain("""{"name":"full","payload":{"$bin":7}}"""));
        Assert.That(sections[4].Content, Is.EqualTo(alphaPayload));
    }

    [Test]
    public async Task FastAndGeneralPathsEmitIdenticalPayloads()
    {
        // The same query through the fast writer and through the general dictionary path: the parts
        // and the payload JSON must match exactly — the placeholder identity the two writers are
        // required to share. A drifted stamp is what reaches the general path here, since this model's
        // DocumentKind carries a renamed value and so the response has an alias table to carry.
        var (fastType, fastBody) = await PostRaw("/api/query", listRequest);
        var fast = ParseMultipart(fastBody, BoundaryOf(fastType));

        var (generalType, generalBody) = await PostRaw("/api/query", driftedRequest);
        var general = ParseMultipart(generalBody, BoundaryOf(generalType));

        Assert.That(fast[..^1].Select(_ => _.Content), Is.EqualTo(general[..^1].Select(_ => _.Content)));

        using var fastEnvelope = JsonDocument.Parse(fast[^1].Content);
        using var generalEnvelope = JsonDocument.Parse(general[^1].Content);
        Assert.That(
            fastEnvelope.RootElement.GetProperty("payload").GetRawText(),
            Is.EqualTo(generalEnvelope.RootElement.GetProperty("payload").GetRawText()));
    }

    Task<(string ContentType, byte[] Body)> PostRaw(string endpoint, string request) =>
        PostRaw(http, endpoint, request);

    static async Task<(string ContentType, byte[] Body)> PostRaw(HttpClient transport, string endpoint, string request)
    {
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await transport.PostAsync(endpoint, content);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), Encoding.UTF8.GetString(body));
        return (response.Content.Headers.ContentType!.ToString(), body);
    }

    // The ndjson lines a section carries. A line always ends in \n, so the trailing empty entry the
    // split leaves is dropped rather than counted as a line.
    static string[] LinesOf((Dictionary<string, string> Headers, byte[] Content) section) =>
        Encoding.UTF8.GetString(section.Content)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    static string BoundaryOf(string contentType)
    {
        Assert.That(contentType, Does.StartWith($"{ScryBinary.ContentType}; boundary={ScryBinary.BoundaryPrefix}"));
        return contentType[$"{ScryBinary.ContentType}; boundary=".Length..];
    }

    // A deliberately independent parse of the framing, so these tests pin the bytes on the wire
    // rather than agreeing with the client's vendored reader by construction.
    static List<(Dictionary<string, string> Headers, byte[] Content)> ParseMultipart(byte[] body, string boundary)
    {
        var sections = new List<(Dictionary<string, string>, byte[])>();
        var span = (ReadOnlySpan<byte>)body;
        var first = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
        var delimiter = Encoding.ASCII.GetBytes($"\r\n--{boundary}");
        Assert.That(span.StartsWith(first), "The body must open with the first boundary line.");
        var index = first.Length;

        while (true)
        {
            var headerEnd = span[index..].IndexOf("\r\n\r\n"u8);
            Assert.That(headerEnd, Is.GreaterThanOrEqualTo(0), "A section must carry headers.");
            var headers = Encoding.ASCII.GetString(span.Slice(index, headerEnd))
                .Split("\r\n")
                .Select(_ => _.Split(':', 2))
                .ToDictionary(_ => _[0].Trim(), _ => _[1].Trim());

            var contentStart = index + headerEnd + 4;
            var contentLength = span[contentStart..].IndexOf(delimiter);
            Assert.That(contentLength, Is.GreaterThanOrEqualTo(0), "A section must end at a delimiter.");
            sections.Add((headers, span.Slice(contentStart, contentLength).ToArray()));

            index = contentStart + contentLength + delimiter.Length;
            if (span[index..].StartsWith("--"u8))
            {
                // The terminator: nothing but the closing line may follow.
                Assert.That(Encoding.ASCII.GetString(span[index..]), Is.EqualTo("--\r\n"));
                return sections;
            }

            Assert.That(span[index..].StartsWith("\r\n"u8), "A delimiter must end its line.");
            index += 2;
        }
    }
}

[Queryable]
public class Document
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [BinaryTransfer]
    public byte[]? Payload { get; set; }

    public DocumentKind Kind { get; set; }
}

// The renamed value gives the schema a non-empty enum-alias table, which is what routes a
// drifted client onto the general fallback path the drift test exercises.
public enum DocumentKind
{
    [PreviousNames("Sketch")]
    Draft,
    Final
}

public sealed class BinaryContext(Microsoft.EntityFrameworkCore.DbContextOptions<BinaryContext> options) :
    Microsoft.EntityFrameworkCore.DbContext(options)
{
    public Microsoft.EntityFrameworkCore.DbSet<Document> Documents { get; set; } = null!;
}
