// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

// ReSharper disable NotAccessedPositionalProperty.Local

/// <summary>
/// The [BinaryTransfer] contract over HTTP, end to end: a byte[] member's values travel as raw
/// multipart parts on all three endpoints, parts precede the JSON that references them, indices are
/// per-document (per row line on the stream, global across a batch), null stays inline, and a
/// binary-free result is plain JSON exactly as before. The fixture is self-contained — the sample
/// model has no binary member — with its own context, schema, and server.
/// </summary>
[TestFixture]
public class BinaryTransferTests
{
    static readonly byte[] alphaPayload = [0x01, 0x02, 0x03];

    // Boundary-shaped content: the delimiter prefix in the part bytes proves the random boundary is
    // never confused by content, and the 0x00/0xFF spread catches any text-mode mangling.
    static readonly byte[] boundaryPayload = [..Encoding.ASCII.GetBytes("\r\n--scry"), 0x00, 0xFF, 0x0D, 0x0A];

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

    const string listRequest =
        """
        {"version":1,"root":"Document","pipeline":[
          {"$type":"orderBy","key":{"$type":"member","path":["Id"]},"descending":false},
          {"$type":"select","projection":{"members":[
            {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Name"]}}},
            {"name":"Payload","value":{"$type":"node","node":{"$type":"member","path":["Payload"]}}}]}}]}
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
        Assert.That(sections[..4].Select(_ => int.Parse(_.Headers["Content-Length"])), Is.EqualTo(new[] {3, boundaryPayload.Length, 0, 256}));

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
                {"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Name"]}}}]}}]}
            """;

        var (single, _) = await PostRaw("/api/query", namesOnly);
        Assert.That(single, Does.StartWith("application/json"));

        var (stream, _) = await PostRaw("/api/query/stream", namesOnly);
        Assert.That(stream, Does.StartWith(ScryStream.ContentType));

        var (batch, _) = await PostRaw("/api/query/batch", $$"""{"version":1,"queries":[{{namesOnly}}]}""");
        Assert.That(batch, Does.StartWith("application/json"));
    }

    [Test]
    public async Task SingleTerminalRoundTripsBinary()
    {
        var row = await Documents
            .Where(_ => _.Name == "full")
            .FirstAsync();

        Assert.That(row.Payload, Is.EqualTo(fullPayload));
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
        drifted.SchemaStamp = "not-the-server's-stamp";

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
            Is.EqualTo(new[]
            {
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType, ScryBinary.PartContentType,
                ScryStream.ContentType
            }));

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
        // The same query through the fast writer (single endpoint, list result) and the general
        // dictionary path (the same list as a batch entry): the parts and the payload JSON must
        // match exactly — the placeholder identity the two writers are required to share.
        var (singleType, singleBody) = await PostRaw("/api/query", listRequest);
        var single = ParseMultipart(singleBody, BoundaryOf(singleType));

        var (batchType, batchBody) = await PostRaw("/api/query/batch", $$"""{"version":1,"queries":[{{listRequest}}]}""");
        var batch = ParseMultipart(batchBody, BoundaryOf(batchType));

        Assert.That(single[..^1].Select(_ => _.Content), Is.EqualTo(batch[..^1].Select(_ => _.Content)));

        using var singleEnvelope = JsonDocument.Parse(single[^1].Content);
        using var batchEnvelope = JsonDocument.Parse(batch[^1].Content);
        var singlePayload = singleEnvelope.RootElement.GetProperty("payload").GetRawText();
        var batchPayload = batchEnvelope.RootElement
            .GetProperty("results")[0]
            .GetProperty("response")
            .GetProperty("payload")
            .GetRawText();
        Assert.That(singlePayload, Is.EqualTo(batchPayload));
    }

    async Task<(string ContentType, byte[] Body)> PostRaw(string endpoint, string request)
    {
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(endpoint, content);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), Encoding.UTF8.GetString(body));
        return (response.Content.Headers.ContentType!.ToString(), body);
    }

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
