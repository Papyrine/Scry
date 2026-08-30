// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ReSharper disable NotAccessedPositionalProperty.Local

/// <summary>
/// The <c>[Attachment]</c> claim check over HTTP, end to end: a query never carries the bytes, the
/// handle it carries instead fetches them from an endpoint of its own, and that endpoint answers only
/// for a row the check authorizes and a row policy allows. Refused, missing, and hidden are one
/// answer; a null value is a different one. The fixture is self-contained — the sample model's
/// attachment is covered by the sample tests — with its own context, schema, and server.
/// </summary>
[TestFixture]
public class AttachmentTests
{
    static readonly byte[] leasePayload = [0x11, 0x22, 0x33];
    static readonly byte[] managerPayload = [..Enumerable.Range(0, 256).Select(_ => (byte) _)];

    // The header the policy refuses on. Client-chosen, and named that way deliberately: a real policy
    // reads identity from the authenticated principal, and this exists to toggle the branch.
    const string denyHeader = "X-Test-Deny";

    static readonly SqlInstance<AttachmentContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: async context =>
        {
            await context.Database.EnsureCreatedAsync();
            context.People.AddRange(
                new() {Id = 1, Name = "Ada", Photo = managerPayload, Resume = leasePayload, Visible = true},
                new() {Id = 2, Name = "Grace", Photo = leasePayload, Visible = true, ManagerId = 1},
                // Readable row, absent value: the 204 case.
                new() {Id = 3, Name = "Alan", Photo = null, Visible = true},
                // Hidden by the row policy, so its attachment is unreachable too.
                new() {Id = 4, Name = "Hidden", Photo = leasePayload, Visible = false});
            await context.SaveChangesAsync();
        });

    WebApplication app = null!;
    HttpClient http = null!;
    ScryClient client = null!;
    SqlDatabase<AttachmentContext> database = null!;

    /// <summary>
    /// Stands in for the generated model. Written exactly as the generator would emit it: the
    /// attachment is absent from the member list, and the key it is fetched by is named instead.
    /// </summary>
    [ScryModel("Person", "Id", "Name", Keys = ["Id"], Attachments = ["Photo", "Resume"])]
    class PersonModel
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public ScryAttachment Photo { get; init; } = null!;
        public ScryAttachment Resume { get; init; } = null!;
        public PersonModel? Manager { get; init; }
    }

    record PersonRow(int Id, ScryAttachment Photo);

    record ManagerRow(string Name, ManagerCard Manager);

    record ManagerCard(int Id, ScryAttachment Photo);

    static readonly string[] personMembers = ["Id", "Name"];

    static readonly string[] seededNames = ["Ada", "Grace", "Alan"];

    IQueryable<PersonModel> People =>
        client.Source<PersonModel>("Person", personMembers);

    [OneTimeSetUp]
    public async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AttachmentContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<AttachmentContext>(_ => { });

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

    static async Task<byte[]?> Read(ScryAttachment attachment)
    {
        await using var stream = await attachment.OpenAsync();
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Test]
    public async Task WholeModelRoundTripsThroughTheHandle()
    {
        var rows = await People.OrderBy(_ => _.Id).ToListAsync();

        // The bytes are read after the query is long finished, which is the whole point: the handle
        // outlives the response it came from, and holds only the key.
        var photo = await Read(rows[0].Photo);

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(_ => _.Name), Is.EqualTo(seededNames));
            Assert.That(photo, Is.EqualTo(managerPayload));
        });
    }

    [Test]
    public async Task ProjectionRoundTripsThroughTheHandle()
    {
        var rows = await People.Where(_ => _.Id == 2)
            .Select(_ => new PersonRow(_.Id, _.Photo))
            .ToListAsync();

        Assert.That(await Read(rows.Single().Photo), Is.EqualTo(leasePayload));
    }

    // The attachment hangs off a navigation, so its key is the navigation's key rather than the row's.
    [Test]
    public async Task NavigationAttachmentRoundTrips()
    {
        var rows = await People.Where(_ => _.Id == 2)
            .Select(_ => new ManagerRow(_.Name, new(_.Manager!.Id, _.Manager!.Photo)))
            .ToListAsync();

        var row = rows.Single();

        Assert.Multiple(async () =>
        {
            Assert.That(row.Name, Is.EqualTo("Grace"));
            Assert.That(row.Manager.Id, Is.EqualTo(1));
            Assert.That(await Read(row.Manager.Photo), Is.EqualTo(managerPayload));
        });
    }

    [Test]
    public async Task StreamedRowsCarryTheHandle()
    {
        var names = new List<string>();
        byte[]? first = null;
        await foreach (var row in People.OrderBy(_ => _.Id).ToAsyncEnumerable())
        {
            names.Add(row.Name);
            first ??= await Read(row.Photo);
        }

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.EqualTo(seededNames));
            Assert.That(first, Is.EqualTo(managerPayload));
        });
    }

    // A row that is there holding no value. Distinct from the refusals: the caller may read it, and
    // what it reads is nothing.
    [Test]
    public async Task NullValueReadsAsNull()
    {
        var row = await People.FirstAsync(_ => _.Id == 3);

        Assert.That(await Read(row!.Photo), Is.Null);
    }

    [Test]
    public async Task DeniedFetchIsNotFound()
    {
        var row = await People.FirstAsync(_ => _.Id == 1);
        http.DefaultRequestHeaders.Add(denyHeader, "yes");
        try
        {
            var exception = Assert.ThrowsAsync<ScryRequestException>(() => Read(row!.Photo));
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            http.DefaultRequestHeaders.Remove(denyHeader);
        }
    }

    [Test]
    public void MissingRowIsNotFound()
    {
        var exception = Assert.ThrowsAsync<ScryRequestException>(
            () => PostAttachment(AttachmentRequest.Create("Person", "Photo", [new("404", ClrTypeTag.Int32)])));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // The row exists and holds a value, but the row policy hides it — so the attachment is as
    // unreachable as the row, and by the same status as a row that was never there.
    [Test]
    public void PolicyFilteredRowIsNotFound()
    {
        var exception = Assert.ThrowsAsync<ScryRequestException>(
            () => PostAttachment(AttachmentRequest.Create("Person", "Photo", [new("4", ClrTypeTag.Int32)])));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void UnknownMemberIsRejected()
    {
        var exception = Assert.ThrowsAsync<ScryRequestException>(
            () => PostAttachment(AttachmentRequest.Create("Person", "Name", [new("1", ClrTypeTag.Int32)])));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Body, Does.Contain("is not an attachment member"));
        });
    }

    [Test]
    public void WrongKeyCountIsRejected()
    {
        var exception = Assert.ThrowsAsync<ScryRequestException>(
            () => PostAttachment(
                AttachmentRequest.Create("Person", "Photo", [new("1", ClrTypeTag.Int32), new("2", ClrTypeTag.Int32)])));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Body, Does.Contain("keyed by 1 value"));
        });
    }

    [Test]
    public void UnparseableKeyIsRejected()
    {
        var exception = Assert.ThrowsAsync<ScryRequestException>(
            () => PostAttachment(AttachmentRequest.Create("Person", "Photo", [new("not-a-number", ClrTypeTag.Int32)])));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Body, Does.Contain("not a valid Int32"));
        });
    }

    // A hand-built request naming the attachment in a query, which the generated client cannot
    // express: the value is not readable by any query, whatever the endpoint.
    [Test]
    public async Task QueryNamingTheAttachmentIsRejected()
    {
        const string json = """
            {
              "version": 1,
              "root": "Person",
              "pipeline": [
                {
                  "$type": "select",
                  "projection": {
                    "members": [
                      { "name": "Photo", "value": { "$type": "node", "node": { "$type": "member", "path": "Photo" } } }
                    ]
                  }
                }
              ]
            }
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>
    /// What the member declared, served as the fetch's content type — and <c>nosniff</c> beside it,
    /// because a declared type is a statement about a column while the bytes under it are whatever was
    /// stored. A browser re-deciding from the content is the one way a wrong label becomes a wrong
    /// behaviour.
    /// </summary>
    [Test]
    public async Task DeclaredContentTypeIsServed()
    {
        using var response = await FetchRaw("Photo", 1);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
            Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
        });
    }

    // An attachment declaring nothing is served as bytes, which is what it was before content types
    // existed: adding the property to one member changes nothing about the next.
    [Test]
    public async Task UndeclaredContentTypeIsOctetStream()
    {
        using var response = await FetchRaw("Resume", 1);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(AttachmentMedia.Default));
        });
    }

    // The row's answer wins over the member's: the policy sees the key before the row is read and can
    // say what this one holds.
    [Test]
    public async Task PolicyOverridesTheDeclaredContentType()
    {
        using var response = await FetchRaw("Photo", PhotoPolicy.JpegId);

        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
    }

    // The response itself rather than its bytes: these assert on headers, which the client's own
    // OpenAsync path deliberately does not surface.
    async Task<HttpResponseMessage> FetchRaw(string member, int id)
    {
        using var content = new StringContent(
            ScryJson.Serialize(AttachmentRequest.Create("Person", member, [new(id.ToString(), ClrTypeTag.Int32)])),
            Encoding.UTF8,
            "application/json");
        return await http.PostAsync("/api/query/attachment", content);
    }

    [Test]
    public async Task EveryStatusCarriesTheSchemaStamp()
    {
        var stamp = app.Services.GetRequiredService<ScryProcessor>().SchemaStamp;

        // 200, 204, 404 and 400 in turn: a client watching for drift has to be able to see it on a
        // response that failed, which is exactly when it most wants to know.
        foreach (var (keys, member) in new (string Key, string Member)[]
                 {
                     ("1", "Photo"),
                     ("3", "Photo"),
                     ("404", "Photo"),
                     ("1", "Name")
                 })
        {
            using var content = new StringContent(
                ScryJson.Serialize(AttachmentRequest.Create("Person", member, [new(keys, ClrTypeTag.Int32)])),
                Encoding.UTF8,
                "application/json");
            using var response = await http.PostAsync("/api/query/attachment", content);

            Assert.That(
                response.Headers.GetValues(WireFormat.SchemaStampHeader).Single(),
                Is.EqualTo(stamp),
                $"key {keys}, member {member} → {(int) response.StatusCode}");
        }
    }

    /// <summary>
    /// The endpoint is mapped inside <c>MapScry</c>, so a convention applied to what it returns
    /// reaches it. A deployment that guards its queries and leaves this open would be handing out by
    /// key exactly what the guard exists to protect.
    /// </summary>
    [Test]
    public async Task AuthorizationReachesTheAttachmentEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AttachmentContext>(_ => _.UseSqlServer(database.ConnectionString));
        builder.Services.AddScry<AttachmentContext>(_ => { });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, RefusingHandler>("Test", _ => { });
        builder.Services.AddAuthorizationBuilder().AddPolicy("Reader", _ => _.RequireAssertion(_ => false));

        var guarded = builder.Build();
        guarded.UseAuthentication();
        guarded.UseAuthorization();
        guarded.MapScry("/api/query").RequireAuthorization("Reader");
        await guarded.StartAsync();

        try
        {
            using var transport = guarded.GetTestClient();
            using var content = new StringContent(
                ScryJson.Serialize(AttachmentRequest.Create("Person", "Photo", [new("1", ClrTypeTag.Int32)])),
                Encoding.UTF8,
                "application/json");
            using var response = await transport.PostAsync("/api/query/attachment", content);

            Assert.That(
                response.StatusCode,
                Is.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden),
                "The attachment endpoint must inherit the authorization applied to MapScry.");
        }
        finally
        {
            await guarded.StopAsync();
            await guarded.DisposeAsync();
        }
    }

    async Task PostAttachment(AttachmentRequest request)
    {
        using var content = new StringContent(ScryJson.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/api/query/attachment", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new ScryRequestException(response.StatusCode, body);
        }
    }

    sealed class RefusingHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) :
        AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}

[Queryable]
[ReturnableWith(typeof(VisiblePeopleOnlyPolicy))]
[AttachmentWith(typeof(PhotoPolicy))]
public class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [Attachment(ContentType = "image/png")]
    public byte[]? Photo { get; set; }

    // The other half of the pair: an attachment declaring nothing, served as bytes and nothing said
    // about them.
    [Attachment]
    public byte[]? Resume { get; set; }

    public bool Visible { get; set; }

    public int? ManagerId { get; set; }
    public Person? Manager { get; set; }
}

/// <summary>Hides one row entirely, so its attachment is unreachable along with it.</summary>
public sealed class VisiblePeopleOnlyPolicy :
    IReturnablePolicy<Person>
{
    public IQueryable<Person> Filter(IQueryable<Person> source, ScryPolicyContext context) =>
        source.Where(_ => _.Visible);
}

/// <summary>
/// Refuses when the caller sends the test header. A real check reads identity from the authenticated
/// principal off <see cref="ScryAttachmentContext.Services"/>; a header is client-chosen and is used
/// here only because a test needs to toggle the branch per call.
/// </summary>
public sealed class PhotoPolicy :
    IAttachmentPolicy<Person>
{
    /// <summary>The seeded row whose photo the policy relabels, exercising the per-row override.</summary>
    public const int JpegId = 2;

    public bool Authorize(ScryAttachmentContext context)
    {
        // What this row's bytes are, decided by the row rather than by the member: the declared
        // image/png stands for every other one. A real model reads it off a sibling column.
        if (context is {Member: "Photo", KeyValues: [JpegId]})
        {
            context.ContentType = "image/jpeg";
        }

        return !context.RequestHeaders.ContainsKey("X-Test-Deny");
    }
}

public sealed class AttachmentContext(Microsoft.EntityFrameworkCore.DbContextOptions<AttachmentContext> options) :
    Microsoft.EntityFrameworkCore.DbContext(options)
{
    public Microsoft.EntityFrameworkCore.DbSet<Person> People { get; set; } = null!;

    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder builder) =>
        // Ids are seeded explicitly, since the tests fetch by key and one of them is refused by id.
        builder.Entity<Person>()
            .Property(_ => _.Id)
            .ValueGeneratedNever();
}
