/// <summary>
/// What the <c>ETag</c> says about the server. It is stored by the caller for as long as it caches,
/// so the parts that identify a response — the freshness token and the cache scope — are fingerprinted
/// rather than written in, the way the query already is.
/// </summary>
[TestFixture]
public class QueryEtagTests
{
    [Test]
    public async Task DoesNotCarryTheScopeOrTheFreshnessTokenVerbatim()
    {
        var context = Request();
        var etag = TagFor(context, "log-position-0000123", "tenant-42");

        // Sent back as the condition, so the tag the test computed is proven to be the one the server
        // answers 304 with.
        context.Request.Headers.IfNoneMatch = etag;
        var notModified = await QueryEtag.NotModified(context, SharedProcessor.Instance, Options("log-position-0000123", "tenant-42"));

        Assert.Multiple(() =>
        {
            Assert.That(notModified, Is.True);
            Assert.That(context.Response.Headers.ETag.ToString(), Is.EqualTo(etag));
            Assert.That(etag, Does.StartWith("\"").And.EndWith("\""));
            Assert.That(etag, Does.Not.Contain("tenant-42"));
            Assert.That(etag, Does.Not.Contain("log-position"));
            Assert.That(etag, Does.Contain(SharedProcessor.Instance.SchemaStamp));
        });
    }

    [Test]
    public void DifferentScopesGetDifferentTags()
    {
        var first = TagFor(Request(), "fresh", "tenant-1");
        var second = TagFor(Request(), "fresh", "tenant-2");

        Assert.That(first, Is.Not.EqualTo(second));
    }

    // A bare "*" would answer 304 to a request whose query was never decoded — including one the
    // validator would have refused — so it is not a match here, whatever the RFC says it stands for.
    [Test]
    public async Task AWildcardConditionIsNotAMatch()
    {
        var context = Request();
        context.Request.Headers.IfNoneMatch = "*";

        var notModified = await QueryEtag.NotModified(context, SharedProcessor.Instance, Options("fresh", "tenant-1"));

        Assert.Multiple(() =>
        {
            Assert.That(notModified, Is.False);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        });
    }

    static DefaultHttpContext Request()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.QueryString = new($"?q={QueryUrl.Encode(QueryRequest.Create("Employee", [new CountOp()]))}");
        return context;
    }

    static ScryOptions Options(string freshness, string scope) =>
        new(typeof(TestContext))
        {
            QueryFreshness = (_, _) => ValueTask.FromResult<string?>(freshness),
            CacheScope = _ => scope
        };

    static string TagFor(HttpContext context, string freshness, string scope) =>
        QueryEtag.Etag(SharedProcessor.Instance.SchemaStamp, freshness, QueryEtag.Query(context.Request)!, scope);
}
