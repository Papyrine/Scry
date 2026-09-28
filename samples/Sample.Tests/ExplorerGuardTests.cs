/// <summary>
/// The explorer's guards under their defaults, which the sample never exercises: it sets
/// <c>EnableGuard</c> to always-on so the browser suite can reach it. Outside Development every
/// route — the host page, introspection, the SQL preview, and every asset — is a 404, the same
/// answer as an explorer that was never mapped. Plus the SQL preview's own guard, the paths a caller
/// might try to walk out of the asset catalogue with, and the preview's content-type rule.
/// </summary>
[NotInParallel]
public class ExplorerGuardTests
{
    static ScryTestServer production = null!;
    static ScryTestServer development = null!;
    static ScryTestServer previewOff = null!;

    // Three servers from the one member, so each is told which database is its own: two of them would
    // otherwise be handed a name a live server already holds, which re-clones it underneath that server.
    [Before(Class)]
    public static async Task StartServers()
    {
        production = await ScryTestServer
            .StartAsync(
                environment: "Production",
                explorer: _ =>
                {
                },
                databaseSuffix: "production");
        development = await ScryTestServer
            .StartAsync(
                environment: "Development",
                explorer: _ =>
                {
                },
                databaseSuffix: "development");
        // The SQL preview turned off on its own: the guard lets the explorer in, the preview's guard
        // keeps SQL out.
        previewOff = await ScryTestServer.StartAsync(
            environment: "Development",
            explorer: _ => _.EnableSqlPreview = _ => false,
            databaseSuffix: "previewOff");
    }

    [After(Class)]
    public static async Task StopServers()
    {
        await production.DisposeAsync();
        await development.DisposeAsync();
        await previewOff.DisposeAsync();
    }

    [Test]
    [Arguments("/scry")]
    [Arguments("/scry/")]
    [Arguments("/scry/introspect")]
    [Arguments("/scry/_framework/blazor.boot.json")]
    [Arguments("/scry/index.html")]
    public async Task OutsideDevelopmentEveryRouteIsNotFound(string path)
    {
        using var http = production.CreateClient();
        using var response = await http.GetAsync(path);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task OutsideDevelopmentTheSqlPreviewIsNotFound()
    {
        using var http = production.CreateClient();
        using var response = await http.PostAsync("/scry/sql", Json());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task InDevelopmentTheDefaultsLetTheExplorerIn()
    {
        using var http = development.CreateClient();
        using var page = await http.GetAsync("/scry");
        using var sql = await http.PostAsync("/scry/sql", Json());

        using (Assert.Multiple())
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(sql.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task ASqlPreviewGuardOfItsOwnKeepsSqlOutWhileTheExplorerIsIn()
    {
        using var http = previewOff.CreateClient();
        using var page = await http.GetAsync("/scry");
        using var introspection = await http.GetAsync("/scry/introspect");
        using var sql = await http.PostAsync("/scry/sql", Json());
        var described = ScryJson.DeserializeIntrospection(await introspection.Content.ReadAsStringAsync());

        using (Assert.Multiple())
        {
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(introspection.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(described.SqlPreview).IsFalse();
            await Assert.That(sql.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    // The assets are manifest resources read by name; nothing about a path reaches a file system. A
    // path that tries to walk out of the catalogue is a 404, or refused by the host before routing,
    // and never a file.
    [Test]
    [Arguments("/scry/%2e%2e/appsettings.json")]
    [Arguments("/scry/..%5cappsettings.json")]
    [Arguments("/scry/_framework/%2e%2e/%2e%2e/appsettings.json")]
    [Arguments("/scry/_framework%5c..%5cindex.html")]
    public async Task AnAssetPathCannotLeaveTheCatalogue(string path)
    {
        using var http = development.CreateClient();
        using var response = await http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Or.IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(body).DoesNotContain("ConnectionStrings");
        }
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

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
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
