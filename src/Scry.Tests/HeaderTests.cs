using System.Net;

/// <summary>
/// Headers are attached to one query rather than to the client, and they are a transport concern
/// throughout: nothing about them reaches the wire request, so the server learns of them only as HTTP
/// headers, and a policy reads them the same way any middleware would.
/// </summary>
[TestFixture]
public class HeaderTests
{
    [Test]
    public async Task HeaderReachesTheOutgoingRequest()
    {
        string? sent = null;
        var client = StubbedClient(
            request =>
            {
                sent = request.Headers.GetValues("X-Correlation").Single();
                return Scalar(3);
            });

        await client.Source<Employee>("Employee", ["Name"])
            .WithHeader("X-Correlation", "abc-123")
            .CountAsync();

        Assert.That(sent, Is.EqualTo("abc-123"));
    }

    // The operator can sit anywhere: it swaps the provider and leaves the captured expression alone,
    // so the operators written around it still translate.
    [Test]
    public async Task HeadersAttachAnywhereInTheChain()
    {
        List<string> sent = [];
        var client = StubbedClient(
            request =>
            {
                sent = [.. request.Headers.GetValues("X-Correlation")];
                return Scalar(1);
            });

        await client.Source<Employee>("Employee", ["Name"])
            .WithHeader("X-Correlation", "first")
            .Where(_ => _.Active)
            .WithHeader("X-Correlation", "second")
            .CountAsync();

        Assert.That(sent, Is.EqualTo(["first", "second"]));
    }

    // A query re-sent as a body after a 405 is the same query asked again, so it carries the headers
    // the first attempt was configured with rather than running the hook a second time — a value the
    // hook mints is the same on both attempts.
    [Test]
    public async Task HeadersAreConfiguredOncePerQuery()
    {
        var sent = new List<string>();
        var minted = 0;
        var client = StubbedClient(
            request =>
            {
                sent.Add(request.Headers.GetValues("X-Request-Id").Single());
                return request.Method == HttpMethod.Get
                    ? new(HttpStatusCode.MethodNotAllowed)
                    : Scalar(3);
            });

        await client.Source<Employee>("Employee", ["Name"])
            .WithHeaders(_ =>
            {
                minted++;
                _.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString());
            })
            .CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sent, Has.Count.EqualTo(2));
            Assert.That(sent.Distinct().Count(), Is.EqualTo(1));
            Assert.That(minted, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ResponseHeadersAreRead()
    {
        var client = StubbedClient(
            _ =>
            {
                var response = Scalar(3);
                response.Headers.TryAddWithoutValidation("X-Trace", "trace-1");
                return response;
            });

        string? trace = null;
        await client.Source<Employee>("Employee", ["Name"])
            .OnResponseHeaders(_ => trace = _.GetValues("X-Trace").Single())
            .CountAsync();

        Assert.That(trace, Is.EqualTo("trace-1"));
    }

    // The response that went wrong is exactly the one whose trace header is worth having, so the hook
    // has to run before the failure is turned into an exception.
    [Test]
    public void ResponseHeadersAreReadWhenTheQueryFails()
    {
        var client = StubbedClient(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new ScryError("nope"), ScryJson.Options))
                };
                response.Headers.TryAddWithoutValidation("X-Trace", "trace-2");
                return response;
            });

        string? trace = null;
        var query = client.Source<Employee>("Employee", ["Name"])
            .OnResponseHeaders(_ => trace = _.GetValues("X-Trace").Single());

        Assert.ThrowsAsync<ScryRequestException>(() => query.CountAsync());
        Assert.That(trace, Is.EqualTo("trace-2"));
    }

    // Headers are HTTP's, and the wire request is what the server validates. A header that leaked into
    // it would be an attacker-supplied value reaching the validator.
    [Test]
    public void HeadersNeverReachTheWireRequest()
    {
        var client = StubbedClient(_ => Scalar(0));
        var plain = client.Source<Employee>("Employee", ["Name"]).Where(_ => _.Active);

        var withHeaders = plain
            .WithHeader("X-Correlation", "abc-123")
            .OnResponseHeaders(_ => { });

        Assert.That(
            ScryJson.Serialize(withHeaders.ToScryRequest()),
            Is.EqualTo(ScryJson.Serialize(plain.ToScryRequest())));
    }

    // A custom transport has nowhere to put a header. Refusing is the honest answer; sending the query
    // without it would make WithHeader look like it worked.
    [Test]
    public void HeadersOverANonHttpTransportAreRefused()
    {
        using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;
        var client = new ScryClient((request, _) => Task.FromResult(processor.Execute(request, context)));

        var query = client.Source<Employee>("Employee", ["Name"])
            .WithHeader("X-Correlation", "abc-123");

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => query.CountAsync())!;
        Assert.That(exception.Message, Does.Contain("ScryClient.ForHttp"));
    }

    [Test]
    public void PolicyReadsTheRequestHeaderAndWritesToTheResponse()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

        var requestHeaders = new HeaderDictionary {["X-Department"] = "Sales"};
        var responseHeaders = new HeaderDictionary();

        var response = Processor().Execute(
            request,
            context,
            EmptyProvider.Instance,
            requestHeaders,
            responseHeaders);

        // Only the Sales employees, so the policy read the header rather than ignoring it.
        Assert.That(ScryJson.Serialize(response), Does.Contain("Bob").And.Contain("Carol"));
        Assert.That(ScryJson.Serialize(response), Does.Not.Contain("Alice"));
        Assert.That(responseHeaders["X-Scry-Policy"].ToString(), Is.EqualTo("department"));
    }

    // The processor is usable off the HTTP endpoint, where there are no headers at all. A policy that
    // reads one there gets an empty dictionary rather than a null reference.
    [Test]
    public void PolicyOutsideTheHttpEndpointSeesEmptyHeaders()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]);

        var response = Processor().Execute(request, context);

        // No X-Department, so the policy filtered nothing and every employee came back.
        Assert.That(ScryJson.Serialize(response), Does.Contain("Alice").And.Contain("Bob"));
    }

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.AddPolicy<Employee, DepartmentHeaderPolicy>();
            });

    static ScryClient StubbedClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new("http://localhost")
        };

        return ScryClient.ForHttp(http, "/api/query");
    }

    static HttpResponseMessage Scalar(int value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ScryJson.Serialize(
                    QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(value))))
        };

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel) =>
            Task.FromResult(respond(request));
    }

    sealed class EmptyProvider :
        IServiceProvider
    {
        public static IServiceProvider Instance { get; } = new EmptyProvider();

        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>
/// A row policy that scopes employees to the department the caller named in a header, and records on
/// the response that it ran. The header is untrusted — this is a test of the plumbing, not a pattern
/// for authorization, which must never key off a value the client chose.
/// </summary>
public sealed class DepartmentHeaderPolicy :
    IReturnablePolicy<Employee>
{
    public IQueryable<Employee> Filter(IQueryable<Employee> source, ScryPolicyContext context)
    {
        context.ResponseHeaders["X-Scry-Policy"] = "department";

        var department = context.RequestHeaders["X-Department"].ToString();
        if (department.Length == 0)
        {
            return source;
        }

        return source.Where(_ => _.Department!.Name == department);
    }
}
