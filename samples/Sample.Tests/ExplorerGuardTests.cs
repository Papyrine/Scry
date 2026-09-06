/// <summary>
/// The explorer's guards under their defaults, which the sample never exercises: it sets
/// <c>EnableGuard</c> to always-on so the browser suite can reach it. Outside Development every
/// route — the host page, introspection, the SQL preview, and every asset — is a 404, the same
/// answer as an explorer that was never mapped. Plus the SQL preview's own guard, the paths a caller
/// might try to walk out of the asset catalogue with, and the preview's content-type rule.
/// </summary>
[TestFixture]
public class ExplorerGuardTests
{
    ScryTestServer production = null!;
    ScryTestServer development = null!;
    ScryTestServer previewOff = null!;

    [OneTimeSetUp]
    public async Task StartServers()
    {
        production = await ScryTestServer
            .StartAsync(
                environment: "Production",
                explorer: _ =>
                {
                });
        development = await ScryTestServer
            .StartAsync(
                environment: "Development",
                explorer: _ =>
                {
                });
        // The SQL preview turned off on its own: the guard lets the explorer in, the preview's guard
        // keeps SQL out.
        previewOff = await ScryTestServer.StartAsync(
            environment: "Development",
            explorer: _ => _.EnableSqlPreview = _ => false);
    }

    [OneTimeTearDown]
    public async Task StopServers()
    {
        await production.DisposeAsync();
        await development.DisposeAsync();
        await previewOff.DisposeAsync();
    }

    [TestCase("/scry")]
    [TestCase("/scry/")]
    [TestCase("/scry/introspect")]
    [TestCase("/scry/_framework/blazor.boot.json")]
    [TestCase("/scry/index.html")]
    public async Task OutsideDevelopmentEveryRouteIsNotFound(string path)
    {
        using var http = production.CreateClient();
        using var response = await http.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task OutsideDevelopmentTheSqlPreviewIsNotFound()
    {
        using var http = production.CreateClient();
        using var response = await http.PostAsync("/scry/sql", Json());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task InDevelopmentTheDefaultsLetTheExplorerIn()
    {
        using var http = development.CreateClient();
        using var page = await http.GetAsync("/scry");
        using var sql = await http.PostAsync("/scry/sql", Json());

        Assert.Multiple(() =>
        {
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(sql.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task ASqlPreviewGuardOfItsOwnKeepsSqlOutWhileTheExplorerIsIn()
    {
        using var http = previewOff.CreateClient();
        using var page = await http.GetAsync("/scry");
        using var introspection = await http.GetAsync("/scry/introspect");
        using var sql = await http.PostAsync("/scry/sql", Json());
        var described = ScryJson.DeserializeIntrospection(await introspection.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(introspection.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(described.SqlPreview, Is.False);
            Assert.That(sql.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    // The assets are manifest resources read by name; nothing about a path reaches a file system. A
    // path that tries to walk out of the catalogue is a 404, or refused by the host before routing,
    // and never a file.
    [TestCase("/scry/%2e%2e/appsettings.json")]
    [TestCase("/scry/..%5cappsettings.json")]
    [TestCase("/scry/_framework/%2e%2e/%2e%2e/appsettings.json")]
    [TestCase("/scry/_framework%5c..%5cindex.html")]
    public async Task AnAssetPathCannotLeaveTheCatalogue(string path)
    {
        using var http = development.CreateClient();
        using var response = await http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest));
            Assert.That(body, Does.Not.Contain("ConnectionStrings"));
        });
    }

    // The same rule the query endpoints apply: a form cannot send application/json, so requiring it
    // keeps a cross-site navigation from reaching the preview.
    [Test]
    public async Task TheSqlPreviewRefusesABodyThatIsNotJson()
    {
        using var http = development.CreateClient();
        var memberNode = new MemberNode(["Name"]);
        var nodeValue = new NodeValue(memberNode);
        using var content = new StringContent(
            ScryJson.Serialize(
                QueryRequest.Create(
                    "Employee",
                    [new SelectOp(new([new("Name", nodeValue)]))])), Encoding.UTF8, "text/plain");
        using var response = await http.PostAsync("/scry/sql", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    static StringContent Json()
    {
        var memberNode = new MemberNode(["Name"]);
        return new(
            ScryJson.Serialize(
                QueryRequest.Create(
                    "Employee",
                    [
                        new SelectOp(
                            new([new("Name", new NodeValue(memberNode))]))
                    ])),
            Encoding.UTF8,
            "application/json");
    }
}
