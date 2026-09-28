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
[NotInParallel]
[DependsOn<HttpRoundTripTests.StaleClient>(nameof(HttpRoundTripTests.StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
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

    static WebApplication app = null!;
    static HttpClient http = null!;
    static ScryClient client = null!;
    static SqlDatabase<BinaryContext> database = null!;

    record Doc(int Id, string Name, byte[]? Payload, DocumentKind Kind);

    static readonly string[] docMembers = ["Id", "Name", "Payload", "Kind"];

    static IQueryable<Doc> Documents =>
        client.Source<Doc>("Document", docMembers);

    [Before(Class)]
    public static async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<BinaryContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<BinaryContext>(_ => _.AllowUnmappedSources = true);

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        client = ScryClient.ForHttp(http, "/api/query");
    }

    [After(Class)]
    public static async Task StopServer()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        http.Dispose();
        await database.DisposeAsync();
    }

    // A row as text, so rows compare by the bytes they carry: a tuple holding an array compares it by
    // reference.
    static string Described(string name, byte[]? payload)
    {
        if (payload is null)
        {
            return $"{name}: null";
        }

        return $"{name}: {Convert.ToHexString(payload)}";
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

        await Assert.That(rows.Select(_ => Described(_.Name, _.Payload))).IsEquivalentTo(seeded.Select(_ => Described(_.Name, _.Payload)), CollectionOrdering.Matching);
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
        var boundary = await BoundaryOf(contentType);
        var sections = await ParseMultipart(body, boundary);

        // Four non-null payloads → four parts, in row order, each byte-exact — then the envelope.
        await Assert.That(sections).Count().IsEqualTo(5);
        await Assert.That(sections[..4].Select(_ => _.Headers["Content-Type"])).All(_ => Equals(_, ScryBinary.PartContentType));
        await Assert.That(sections[0].Content).IsEquivalentTo(alphaPayload, CollectionOrdering.Matching);
        await Assert.That(sections[1].Content).IsEquivalentTo(boundaryPayload, CollectionOrdering.Matching);
        await Assert.That(sections[2].Content).IsEmpty();
        await Assert.That(sections[3].Content).IsEquivalentTo(fullPayload, CollectionOrdering.Matching);
        await Assert.That(sections[..4].Select(_ => int.Parse(_.Headers["Content-Length"]))).IsEquivalentTo(new[] {3, boundaryPayload.Length, 0, 256}, CollectionOrdering.Matching);

        await Assert.That(sections[4].Headers["Content-Type"]).IsEqualTo("application/json");
        var envelope = Encoding.UTF8.GetString(sections[4].Content);
        // Placeholders number the parts in emission order; a null value stays inline and takes no index.
        await Assert.That(envelope).Contains("""{"name":"alpha","payload":{"$bin":0}}""");
        await Assert.That(envelope).Contains("""{"name":"boundary","payload":{"$bin":1}}""");
        await Assert.That(envelope).Contains("""{"name":"empty","payload":{"$bin":2}}""");
        await Assert.That(envelope).Contains("""{"name":"missing","payload":null}""");
        await Assert.That(envelope).Contains("""{"name":"full","payload":{"$bin":3}}""");
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
        await Assert.That(single).StartsWith("application/json");

        var (stream, _) = await PostRaw("/api/query/stream", namesOnly);
        await Assert.That(stream).StartsWith(ScryStream.ContentType);

        var (batch, _) = await PostRaw("/api/query/batch", $$"""{"version":1,"queries":[{{namesOnly}}]}""");
        await Assert.That(batch).StartsWith("application/json");
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
        var sections = await ParseMultipart(body, await BoundaryOf(contentType));

        // The framing this fixture already pins, arrived at with spilling switched on as hard as it goes.
        await Assert.That(sections).Count().IsEqualTo(5);
        await Assert.That(sections[0].Content).IsEquivalentTo(alphaPayload, CollectionOrdering.Matching);
        await Assert.That(sections[3].Content).IsEquivalentTo(fullPayload, CollectionOrdering.Matching);
        await Assert.That(sections[4].Headers["Content-Type"]).IsEqualTo("application/json");
        await Assert.That(Encoding.UTF8.GetString(sections[4].Content)).Contains("""{"name":"alpha","payload":{"$bin":0}}""");
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

        using (Assert.Multiple())
        {
            await Assert.That(response.Content.Headers.ContentType!.ToString()).StartsWith("application/json");
            await Assert.That(declared).IsNull();
            await Assert.That(body).Contains("""{"name":"alpha"}""");
            await Assert.That(body).EndsWith("}");
        }
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
        await Assert.That(declared).IsEqualTo(body.Length);
    }

    [Test]
    public async Task SingleTerminalRoundTripsBinary()
    {
        var row = await Documents
            .Where(_ => _.Name == "full")
            .FirstAsync();

        await Assert.That(row!.Payload).IsEquivalentTo(fullPayload, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PageTerminalRoundTripsBinary()
    {
        var page = await Documents
            .OrderBy(_ => _.Id)
            .ToPageAsync(3);

        await Assert.That(page.Items.Select(_ => Described(_.Name, _.Payload))).IsEquivalentTo(seeded[..3].Select(_ => Described(_.Name, _.Payload)), CollectionOrdering.Matching);
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

        await Assert.That(rows.Select(_ => Described(_.Name, _.Payload))).IsEquivalentTo(seeded.Select(_ => Described(_.Name, _.Payload)), CollectionOrdering.Matching);
    }

    [Test]
    public async Task StreamRoundTripsBinaryPerRow()
    {
        var rows = new List<Doc>();
        await foreach (var row in Documents.OrderBy(_ => _.Id).ToAsyncEnumerable())
        {
            rows.Add(row);
        }

        await Assert.That(rows.Select(_ => Described(_.Name, _.Payload))).IsEquivalentTo(seeded.Select(_ => Described(_.Name, _.Payload)), CollectionOrdering.Matching);
    }

    [Test]
    public async Task StreamAlternatesSectionsAndResetsIndicesPerRow()
    {
        var (contentType, body) = await PostRaw("/api/query/stream", listRequest);
        await Assert.That(contentType).StartsWith(ScryBinary.ContentType);
        var sections = await ParseMultipart(body, await BoundaryOf(contentType));

        // Ndjson sections alternate with each row's parts: begin | part | alpha-row | part |
        // boundary-row | part | empty-row + partless missing-row | part | full-row + end.
        await Assert.That(sections.Select(_ => _.Headers["Content-Type"])).IsEquivalentTo([
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType
            ], CollectionOrdering.Matching);

        await Assert.That(sections[1].Content).IsEquivalentTo(alphaPayload, CollectionOrdering.Matching);
        await Assert.That(sections[3].Content).IsEquivalentTo(boundaryPayload, CollectionOrdering.Matching);
        await Assert.That(sections[5].Content).IsEmpty();
        await Assert.That(sections[7].Content).IsEquivalentTo(fullPayload, CollectionOrdering.Matching);

        var lines = sections
            .Where(_ => _.Headers["Content-Type"] == ScryStream.ContentType)
            .Select(_ => Encoding.UTF8.GetString(_.Content))
            .ToArray();
        await Assert.That(lines[0]).Contains(ScryStream.MarkerProperty).And.Contains(ScryStream.Begin);
        // Every row's placeholder is index 0 again: a stream's indices reset per row line.
        await Assert.That(lines[1]).Contains("""{"name":"alpha","payload":{"$bin":0}}""");
        await Assert.That(lines[2]).Contains("""{"name":"boundary","payload":{"$bin":0}}""");
        // The partless row rides the same section as the row before it.
        await Assert.That(lines[3]).Contains("""{"name":"empty","payload":{"$bin":0}}""");
        await Assert.That(lines[3]).Contains("""{"name":"missing","payload":null}""");
        await Assert.That(lines[4]).Contains("""{"name":"full","payload":{"$bin":0}}""");
        await Assert.That(lines[4]).Contains(ScryStream.End);
    }

    [Test]
    public async Task StreamCommitsToMultipartBeforeTheFirstRow()
    {
        // The framing is the plan's decision, not the data's: the content type is fixed before a row
        // is pulled, so a projection with a binary slot wraps even when nothing ends up diverting.
        // A single null-valued row, and no rows at all, are the two ways that can happen.
        var (nullType, nullBody) = await PostRaw("/api/query/stream", NamedRequest("missing"));
        var nullSections = await ParseMultipart(nullBody, await BoundaryOf(nullType));

        await Assert.That(nullSections.Select(_ => _.Headers["Content-Type"])).IsEquivalentTo([ScryStream.ContentType], CollectionOrdering.Matching);
        var nullLines = LinesOf(nullSections[0]);
        await Assert.That(nullLines).Count().IsEqualTo(3);
        await Assert.That(nullLines[1]).Contains("""{"name":"missing","payload":null}""");

        var (emptyType, emptyBody) = await PostRaw("/api/query/stream", NamedRequest("nothing is named this"));
        var emptySections = await ParseMultipart(emptyBody, await BoundaryOf(emptyType));

        await Assert.That(emptySections.Select(_ => _.Headers["Content-Type"])).IsEquivalentTo([ScryStream.ContentType], CollectionOrdering.Matching);
        // Nothing between the markers: an empty result is still a multipart response, just an empty one.
        var emptyLines = LinesOf(emptySections[0]);
        await Assert.That(emptyLines).Count().IsEqualTo(2);
        await Assert.That(emptyLines[0]).Contains(ScryStream.Begin);
        await Assert.That(emptyLines[1]).Contains(ScryStream.End);
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

        await Assert.That(rows.Select(_ => Described(_.Name, _.Payload))).IsEquivalentTo(seeded.Select(_ => Described(_.Name, _.Payload)), CollectionOrdering.Matching);
    }

    [Test]
    public async Task DriftedStreamCarriesTheAliasesAndKeepsItsParts()
    {
        var (contentType, body) = await PostRaw("/api/query/stream", driftedRequest);
        var sections = await ParseMultipart(body, await BoundaryOf(contentType));

        // A mismatched stamp adds the enum alias table to the begin marker, which is the one thing on
        // this path that differs. Everything around it is the framing a matching client gets: the same
        // alternating sections, the same bytes.
        await Assert.That(sections).Count().IsEqualTo(9);
        await Assert.That(LinesOf(sections[0])[0]).Contains("DocumentKind").And.Contains("Sketch");
        await Assert.That(sections[1].Content).IsEquivalentTo(alphaPayload, CollectionOrdering.Matching);
        await Assert.That(sections[3].Content).IsEquivalentTo(boundaryPayload, CollectionOrdering.Matching);
        await Assert.That(sections[5].Content).IsEmpty();
        await Assert.That(sections[7].Content).IsEquivalentTo(fullPayload, CollectionOrdering.Matching);
    }

    [Test]
    public async Task StreamOverTheRowLimitFailsAfterThePartsAlreadySent()
    {
        await using var limited = await StartLimited(maxStreamRows: 2);
        using var limitedHttp = limited.GetTestClient();

        var (contentType, body) = await PostRaw(limitedHttp, "/api/query/stream", listRequest);
        var sections = await ParseMultipart(body, await BoundaryOf(contentType));

        // Two rows and their parts are on the wire by the time the limit trips, and the status is long
        // since sent — so the failure rides the stream's error marker, in the section the last row was
        // written into, and the closing marker never comes.
        await Assert.That(sections.Select(_ => _.Headers["Content-Type"])).IsEquivalentTo([
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType
            ], CollectionOrdering.Matching);
        await Assert.That(sections[1].Content).IsEquivalentTo(alphaPayload, CollectionOrdering.Matching);
        await Assert.That(sections[3].Content).IsEquivalentTo(boundaryPayload, CollectionOrdering.Matching);

        var last = LinesOf(sections[4]);
        await Assert.That(last[0]).Contains("""{"name":"boundary","payload":{"$bin":0}}""");
        await Assert.That(last[1]).Contains($"\"{ScryStream.MarkerProperty}\":\"{ScryStream.Error}\"");
        await Assert.That(last[1]).Contains("more than the maximum of 2 streamed rows");
        await Assert.That(Encoding.UTF8.GetString(body)).DoesNotContain($"\"{ScryStream.MarkerProperty}\":\"{ScryStream.End}\"");
    }

    [Test]
    public async Task StreamOverTheRowLimitSurfacesTheFailureToTheReader()
    {
        await using var limited = await StartLimited(maxStreamRows: 2);
        using var limitedHttp = limited.GetTestClient();
        var limitedClient = ScryClient.ForHttp(limitedHttp, "/api/query");
        var documents = limitedClient.Source<Doc>("Document", docMembers);

        var rows = new List<Doc>();
        var exception = await Assert.ThrowsExactlyAsync<ScryWireException>(
            async () =>
            {
                await foreach (var row in documents.OrderBy(_ => _.Id).ToAsyncEnumerable())
                {
                    rows.Add(row);
                }
            });

        await Assert.That(exception!.Message).Contains("more than the maximum of 2 streamed rows");
        // The rows that did arrive are whole — their parts were read and resolved before the failure,
        // so a truncated stream is an error rather than a short answer with mangled bytes.
        await Assert.That(rows.Select(_ => Described(_.Name, _.Payload))).IsEquivalentTo(seeded[..2].Select(_ => Described(_.Name, _.Payload)), CollectionOrdering.Matching);
    }

    // A second server, because the spill threshold is fixed at startup and the fixture's own server
    // keeps the default — under which nothing here is large enough to spill at all.
    static async Task<WebApplication> StartSpilling(int threshold)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<BinaryContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<BinaryContext>(_ =>
        {
            _.AllowUnmappedSources = true;
            _.ResponseSpillThreshold = threshold;
        });

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }

    // A second server, because the row limit is fixed at startup and the fixture's own server has none.
    static async Task<WebApplication> StartLimited(int maxStreamRows)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<BinaryContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<BinaryContext>(_ =>
        {
            _.AllowUnmappedSources = true;
            _.MaxStreamRows = maxStreamRows;
        });

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

        await Assert.That((await withBinary).Select(_ => Described(_.Name, _.Payload))).IsEquivalentTo(seeded[..2].Select(_ => Described(_.Name, _.Payload)), CollectionOrdering.Matching);
        await Assert.That((await namesOnly).Select(_ => _.Name)).IsEquivalentTo(seeded.Select(_ => _.Name), CollectionOrdering.Matching);
        await Assert.That((await moreBinary).Single().Payload).IsEquivalentTo(fullPayload, CollectionOrdering.Matching);
    }

    record NameRow(string Name);

    [Test]
    public async Task BatchEnvelopeNumbersPartsGlobally()
    {
        const string request =
            $$"""{"version":1,"queries":[{{listRequest}},{{listRequest}}]}""";

        var (contentType, body) = await PostRaw("/api/query/batch", request);
        await Assert.That(contentType).StartsWith(ScryBinary.ContentType);
        var sections = await ParseMultipart(body, await BoundaryOf(contentType));

        // Two identical entries → eight parts (four each), one envelope, indices continuing across
        // the entry boundary rather than resetting.
        await Assert.That(sections).Count().IsEqualTo(9);
        var envelope = Encoding.UTF8.GetString(sections[8].Content);
        await Assert.That(envelope).Contains("""{"name":"alpha","payload":{"$bin":0}}""");
        await Assert.That(envelope).Contains("""{"name":"full","payload":{"$bin":3}}""");
        await Assert.That(envelope).Contains("""{"name":"alpha","payload":{"$bin":4}}""");
        await Assert.That(envelope).Contains("""{"name":"full","payload":{"$bin":7}}""");
        await Assert.That(sections[4].Content).IsEquivalentTo(alphaPayload, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FastAndGeneralPathsEmitIdenticalPayloads()
    {
        // The same query through the fast writer and through the general dictionary path: the parts
        // and the payload JSON must match exactly — the placeholder identity the two writers are
        // required to share. A drifted stamp is what reaches the general path here, since this model's
        // DocumentKind carries a renamed value and so the response has an alias table to carry.
        var (fastType, fastBody) = await PostRaw("/api/query", listRequest);
        var fast = await ParseMultipart(fastBody, await BoundaryOf(fastType));

        var (generalType, generalBody) = await PostRaw("/api/query", driftedRequest);
        var general = await ParseMultipart(generalBody, await BoundaryOf(generalType));

        await Assert.That(fast[..^1].Select(_ => Convert.ToHexString(_.Content))).IsEquivalentTo(general[..^1].Select(_ => Convert.ToHexString(_.Content)), CollectionOrdering.Matching);

        using var fastEnvelope = JsonDocument.Parse(fast[^1].Content);
        using var generalEnvelope = JsonDocument.Parse(general[^1].Content);
        await Assert.That(fastEnvelope.RootElement.GetProperty("payload").GetRawText()).IsEqualTo(generalEnvelope.RootElement.GetProperty("payload").GetRawText());
    }

    static Task<(string ContentType, byte[] Body)> PostRaw(string endpoint, string request) =>
        PostRaw(http, endpoint, request);

    static async Task<(string ContentType, byte[] Body)> PostRaw(HttpClient transport, string endpoint, string request)
    {
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await transport.PostAsync(endpoint, content);
        var body = await response.Content.ReadAsByteArrayAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(Encoding.UTF8.GetString(body));
        return (response.Content.Headers.ContentType!.ToString(), body);
    }

    // The ndjson lines a section carries. A line always ends in \n, so the trailing empty entry the
    // split leaves is dropped rather than counted as a line.
    static string[] LinesOf((Dictionary<string, string> Headers, byte[] Content) section) =>
        Encoding.UTF8.GetString(section.Content)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    static async Task<string> BoundaryOf(string contentType)
    {
        await Assert.That(contentType).StartsWith($"{ScryBinary.ContentType}; boundary={ScryBinary.BoundaryPrefix}");
        return contentType[$"{ScryBinary.ContentType}; boundary=".Length..];
    }

    // A deliberately independent parse of the framing, so these tests pin the bytes on the wire
    // rather than agreeing with the client's vendored reader by construction.
    static async Task<List<(Dictionary<string, string> Headers, byte[] Content)>> ParseMultipart(byte[] body, string boundary)
    {
        var sections = new List<(Dictionary<string, string>, byte[])>();
        var first = Encoding.ASCII.GetBytes($"--{boundary}\r\n");
        var delimiter = Encoding.ASCII.GetBytes($"\r\n--{boundary}");
        await Assert.That(body.AsSpan().StartsWith(first)).IsTrue().Because("The body must open with the first boundary line.");
        var index = first.Length;

        while (true)
        {
            var headerEnd = body.AsSpan()[index..].IndexOf("\r\n\r\n"u8);
            await Assert.That(headerEnd).IsGreaterThanOrEqualTo(0).Because("A section must carry headers.");
            var headers = Encoding.ASCII.GetString(body.AsSpan().Slice(index, headerEnd))
                .Split("\r\n")
                .Select(_ => _.Split(':', 2))
                .ToDictionary(_ => _[0].Trim(), _ => _[1].Trim());

            var contentStart = index + headerEnd + 4;
            var contentLength = body.AsSpan()[contentStart..].IndexOf(delimiter);
            await Assert.That(contentLength).IsGreaterThanOrEqualTo(0).Because("A section must end at a delimiter.");
            sections.Add((headers, body.AsSpan().Slice(contentStart, contentLength).ToArray()));

            index = contentStart + contentLength + delimiter.Length;
            if (body.AsSpan()[index..].StartsWith("--"u8))
            {
                // The terminator: nothing but the closing line may follow.
                await Assert.That(Encoding.ASCII.GetString(body.AsSpan()[index..])).IsEqualTo("--\r\n");
                return sections;
            }

            await Assert.That(body.AsSpan()[index..].StartsWith("\r\n"u8)).IsTrue().Because("A delimiter must end its line.");
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
