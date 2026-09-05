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
        var options = new ScryOptions(typeof(TestContext))
        {
            QueryFreshness = (_, _) => ValueTask.FromResult<string?>("log-position-0000123"),
            CacheScope = _ => "tenant-42"
        };
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.QueryString = new($"?q={QueryUrl.Encode(QueryRequest.Create("Employee", [new CountOp()]))}");
        // Any current representation matches, so the tag is written on a 304 without a query running.
        context.Request.Headers.IfNoneMatch = "*";

        var notModified = await QueryEtag.NotModified(context, SharedProcessor.Instance, options);
        var etag = context.Response.Headers.ETag.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(notModified, Is.True);
            Assert.That(etag, Does.StartWith("\"").And.EndWith("\""));
            Assert.That(etag, Does.Not.Contain("tenant-42"));
            Assert.That(etag, Does.Not.Contain("log-position"));
            Assert.That(etag, Does.Contain(SharedProcessor.Instance.SchemaStamp));
        });
    }

    [Test]
    public async Task DifferentScopesGetDifferentTags()
    {
        var first = await TagFor("tenant-1");
        var second = await TagFor("tenant-2");

        Assert.That(first, Is.Not.EqualTo(second));
    }

    static async Task<string> TagFor(string scope)
    {
        var options = new ScryOptions(typeof(TestContext))
        {
            QueryFreshness = (_, _) => ValueTask.FromResult<string?>("fresh"),
            CacheScope = _ => scope
        };
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.QueryString = new($"?q={QueryUrl.Encode(QueryRequest.Create("Employee", [new CountOp()]))}");
        context.Request.Headers.IfNoneMatch = "*";
        await QueryEtag.NotModified(context, SharedProcessor.Instance, options);
        return context.Response.Headers.ETag.ToString();
    }
}
