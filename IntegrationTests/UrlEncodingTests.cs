// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
using Microsoft.AspNetCore.Http;

/// <summary>
/// What a query asked as a URL actually looks like on the wire: which encoding the client picks, and
/// what the two debugging options add to it.
/// </summary>
/// <remarks>
/// No database is reached. Every query here reads the poco source, so the connection string is never
/// opened — none of this behaviour is about data.
/// </remarks>
[TestFixture]
public class UrlEncodingTests
{
    WebApplication app = null!;
    HttpClient http = null!;
    ScryClient client = null!;

    // What the server saw, rather than what the client meant to send. The query string is taken raw —
    // still escaped — because escaping is half of what is under test here.
    static string? method;
    static string? queryString;
    static string? body;

    [SetUp]
    public async Task StartServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(
            _ => _.UseSqlServer("Server=(localdb)\\ScryUnused;Database=ScryUnused"));
        builder.Services.AddScry<Sample.Model.SampleContext>(options =>
        {
            options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
            // Department.Handbook is an [Attachment], and startup refuses a source whose attachment
            // nothing authorizes. Nothing here fetches one, so an allow-all satisfies the check.
            options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
        });

        app = builder.Build();

        app.Use(async (context, next) =>
        {
            method = context.Request.Method;
            queryString = context.Request.QueryString.Value;

            // Buffered so the handler can still read it — which, on a GET, is exactly what it does not
            // do, and one of the things these tests pin.
            context.Request.EnableBuffering();
            body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            context.Request.Body.Position = 0;

            await next();
        });

        app.MapScry("/api/query");
        await app.StartAsync();

        http = app.GetTestClient();
        client = ScryClient.ForHttp(http, "/api/query");
    }

    [TearDown]
    public async Task StopServer()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        method = null;
        queryString = null;
        body = null;
    }

    Task<List<HolidayRow>> Run() =>
        client.Source<HolidayQueryModel>("Holiday")
            .Select(_ => new HolidayRow(_.Name))
            .ToListAsync();

    record HolidayRow(string Name);

    // The default: the request is in the URL as itself, so anything that reads URLs reads the query.
    [Test]
    public async Task JsonIsTheDefaultEncoding()
    {
        var rows = await Run();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Not.Empty);
            Assert.That(method, Is.EqualTo("GET"));

            // Escaped on the wire...
            Assert.That(queryString, Does.Not.Contain("{"));

            // ...and the request once that is undone.
            Assert.That(Uri.UnescapeDataString(queryString!), Does.Contain("\"root\":\"Holiday\""));
        });
    }

    // Opt in and the URL is the shorter, opaque form the client used to send always.
    [Test]
    public async Task Base64UrlIsOptIn()
    {
        client.UrlEncoding = QueryUrlEncoding.Base64Url;

        var rows = await Run();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Not.Empty);
            Assert.That(method, Is.EqualTo("GET"));
            Assert.That(queryString, Does.Not.Contain("%"));
            Assert.That(
                queryString![$"?{QueryUrl.Parameter}=".Length..].All(
                    _ => char.IsAsciiLetterOrDigit(_) || _ is '-' or '_'),
                Is.True,
                $"not base64url: {queryString}");
        });
    }

    [Test]
    public async Task NoBodyOnAUrlQueryByDefault()
    {
        await Run();

        Assert.Multiple(() =>
        {
            Assert.That(method, Is.EqualTo("GET"));
            Assert.That(body, Is.Empty);
        });
    }

    // The debugging option: the URL still carries the request, and the body repeats it.
    [Test]
    public async Task TheBodyRepeatsTheUrlWhenAskedFor()
    {
        client.IncludeJsonBodyOnUrlQuery = true;

        var rows = await Run();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Not.Empty);
            Assert.That(method, Is.EqualTo("GET"));
            Assert.That(body, Does.Contain("\"root\":\"Holiday\""));
            Assert.That(body, Is.EqualTo(Uri.UnescapeDataString(queryString![$"?{QueryUrl.Parameter}=".Length..])));
        });
    }

    // The property that makes the option safe to leave on: the body is inert. A hop that drops it
    // changes nothing, and a body disagreeing with the URL cannot change what runs — so this stays a
    // debugging aid rather than a second, unvalidated way to ask.
    [Test]
    public async Task ABodyDisagreeingWithTheUrlIsIgnored()
    {
        var url = QueryUrl.Encode(
            client.Source<HolidayQueryModel>("Holiday")
                .Select(_ => new HolidayRow(_.Name))
                .ToScryRequest());

        using var message = new HttpRequestMessage(HttpMethod.Get, $"/api/query?{QueryUrl.Parameter}={url}")
        {
            Content = new StringContent(
                """{"version":1,"root":"NotASource","pipeline":[]}""",
                Encoding.UTF8,
                "application/json")
        };

        using var response = await http.SendAsync(message);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // The URL named a real source and that is what was answered; the body named one the
            // allow-list would have refused, and was never read.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(payload, Does.Contain("New Year"));
            Assert.That(payload, Does.Not.Contain("NotASource"));
        });
    }
}
