using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The explorer's host page as one mapping serves it: the base href written in, the inline scripts
/// hashed into the <c>Content-Security-Policy</c>, and the entity tag of those same bytes. The
/// browser suite proves the policy lets the real page run; these pin the three against each other,
/// on pages the embedded one deliberately never is.
/// </summary>
[TestFixture]
public class ExplorerPageTests
{
    static ExplorerPage Build(string html, string route = "/scry", string? queryEndpoint = null)
    {
        var options = new ScryExplorerOptions();
        if (queryEndpoint is not null)
        {
            options.QueryEndpoint = queryEndpoint;
        }

        return ScryExplorerExtensions.Build(html, route, options);
    }

    static string Sha256(string script) =>
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";

    /// <summary>
    /// The one the rewrite order exists for. A page that names the base inside a script is hashed
    /// after the substitution, so the hash is of the script the browser will actually parse — hashing
    /// the embedded text instead would name a script that is never served, and the browser would
    /// refuse the one that is.
    /// </summary>
    [Test]
    public void HashesTheScriptAsItIsServedNotAsItIsEmbedded()
    {
        var page = Build("""<script>window.base = '__SCRY_BASE__';</script>""");

        Assert.Multiple(() =>
        {
            Assert.That(page.Html, Does.Contain("window.base = '/scry/';"));
            Assert.That(page.Policy, Does.Contain(Sha256("window.base = '/scry/';")));
            Assert.That(page.Policy, Does.Not.Contain(Sha256("window.base = '__SCRY_BASE__';")));
        });
    }

    /// <summary>Every inline script, in document order; the ones with a src are the origin's to allow.</summary>
    [Test]
    public void HashesEveryInlineScriptAndNoSourcedOne()
    {
        var page = Build(
            """
            <script src="first.js"></script>
            <script>one();</script>
            <script defer src="second.js"></script>
            <script type="module">two();</script>
            """);

        Assert.That(
            page.Policy,
            Does.Contain($"script-src 'self' 'wasm-unsafe-eval' {Sha256("one();")} {Sha256("two();")};"));
    }

    /// <summary>
    /// A host-source has no room for userinfo, so an endpoint carrying credentials has to be reduced
    /// to its origin. Left in, the browser drops the expression as unparseable and refuses every call
    /// the explorer makes to the endpoint it was configured with.
    /// </summary>
    [Test]
    public void ConnectSrcNamesTheEndpointsOriginWithoutItsCredentials()
    {
        var page = Build("<p></p>", queryEndpoint: "https://user:pass@api.example.com:8443/scry/query");

        Assert.Multiple(() =>
        {
            Assert.That(page.Policy, Does.Contain("connect-src 'self' https://api.example.com:8443;"));
            Assert.That(page.Policy, Does.Not.Contain("user:pass"));
        });
    }

    [TestCase("/query", TestName = "a relative endpoint is the origin's own")]
    [TestCase("ftp://api.example.com/query", TestName = "a scheme the page cannot fetch over")]
    public void ConnectSrcIsTheOriginAloneFor(string queryEndpoint)
    {
        var page = Build("<p></p>", queryEndpoint: queryEndpoint);

        Assert.That(page.Policy, Does.Contain("connect-src 'self';"));
    }

    /// <summary>The tag is of the served bytes, so two routes are two pages and two tags.</summary>
    [Test]
    public void TagsWhatIsServedRatherThanWhatIsEmbedded()
    {
        const string html = """<base href="__SCRY_BASE__" />""";

        var mounted = Build(html);
        var elsewhere = Build(html, route: "/tools/scry");

        Assert.Multiple(() =>
        {
            Assert.That(mounted.Html, Does.Contain("""<base href="/scry/" />"""));
            Assert.That(elsewhere.Html, Does.Contain("""<base href="/tools/scry/" />"""));
            Assert.That(mounted.Tag, Is.Not.EqualTo(elsewhere.Tag));
            // Quoted as the header wants it, and the same page tags the same way twice.
            Assert.That(mounted.Tag, Does.StartWith("\"").And.EndWith("\""));
            Assert.That(Build(html).Tag, Is.EqualTo(mounted.Tag));
        });
    }

    /// <summary>
    /// The embedded page through the same builder. What the browser suite proves at the far end, this
    /// pins at the near one: the real page has exactly the two inline scripts the policy names, and
    /// its base href is the route it was mounted at.
    /// </summary>
    [Test]
    public void BuildsTheEmbeddedPage()
    {
        var page = ScryExplorerExtensions.Build(
            ExplorerAssets.Instance.ReadText("index.html"),
            "/tools/scry",
            new());

        Assert.Multiple(() =>
        {
            Assert.That(page.Html, Does.Contain("""<base href="/tools/scry/" />"""));
            Assert.That(page.Html, Does.Not.Contain("__SCRY_BASE__"));
            Assert.That(ExplorerAssets.InlineScriptHashes(page.Html), Has.Count.EqualTo(2));
        });
    }

    /// <summary>
    /// The one asset a mapping cannot start without, and the one that goes missing silently: a build
    /// whose UI publish did not run still produces a package, and the only symptom is an explorer
    /// serving nothing. MapScryExplorer refuses to map without it, and this is what says that guard
    /// is not the thing failing.
    /// </summary>
    [Test]
    public void ThePackageEmbedsItsHostPage() =>
        Assert.That(ExplorerAssets.Instance.HasAssets, Is.True);

    /// <summary>
    /// The root mount. A base href of "//" is a scheme-relative url, which would send the whole app
    /// looking for its assets on a host named by whatever followed.
    /// </summary>
    [Test]
    public void TheRootRouteWritesASingleSlash()
    {
        var page = Build("""<base href="__SCRY_BASE__" />""", route: "/");

        Assert.That(page.Html, Does.Contain("""<base href="/" />"""));
    }
}
