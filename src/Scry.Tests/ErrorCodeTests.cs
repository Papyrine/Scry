/// <summary>
/// What a non-success response says about itself, and what the client does with it. The code is the
/// part a client branches on — the message is for a person — so every answer the endpoints give has
/// one, and the two that have a specific remedy keep exceptions of their own.
/// </summary>
public class ErrorCodeTests
{
    [Test]
    [Arguments(ScryErrorCode.WireFormat)]
    [Arguments(ScryErrorCode.Validation)]
    [Arguments(ScryErrorCode.UnsupportedMedia)]
    [Arguments(ScryErrorCode.ExecutionFailed)]
    public async Task ACodeWithNoRemedyOfItsOwnKeepsTheRequestException(ScryErrorCode code)
    {
        var client = StubbedClient(_ => Failure(HttpStatusCode.BadRequest, "nope", code));

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(() => Count(client)))!;
        await Assert.That(exception.Code).IsEqualTo(code);
    }

    // The two the client acts on rather than reports: one prompts a reload, the other is final.
    [Test]
    public async Task TheStaleClientCodeIsItsOwnException() =>
        await Assert.ThrowsExactlyAsync<ScryStaleClientException>(
            () => Count(StubbedClient(_ => Failure(HttpStatusCode.BadRequest, "drifted", ScryErrorCode.StaleClient))));

    [Test]
    public async Task TheForbiddenCodeIsItsOwnException() =>
        await Assert.ThrowsExactlyAsync<ScryPermissionException>(
            () => Count(StubbedClient(_ => Failure(HttpStatusCode.Forbidden, "denied", ScryErrorCode.Forbidden))));

    // A 403 from something in the way is not a policy denial: the endpoint says so with a code, and
    // nothing else gets to claim one. Without a code there is only the status to report.
    [Test]
    public async Task AForbiddenStatusWithoutTheCodeIsNotADenial()
    {
        var client = StubbedClient(
            _ => new(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":"blocked by the gateway"}""")
            });

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(() => Count(client)))!;
        using (Assert.Multiple())
        {
            await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.Unknown);
            await Assert.That(exception.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    [Test]
    public async Task ABodyThatIsNotAnErrorCarriesNoCode()
    {
        var client = StubbedClient(
            _ => new(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html>502 from a proxy</html>")
            });

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(() => Count(client)))!;
        using (Assert.Multiple())
        {
            await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.Unknown);
            await Assert.That(exception.Body).Contains("502 from a proxy");
        }
    }

    /// <summary>
    /// The guard on the two being different axes. A client generated before a member was marked
    /// <c>[Sensitive]</c> is exactly the client that asks for it in a URL, so the refusal it gets is
    /// attributed to the stamp <em>and</em> carries <c>requiresBody</c> — and re-sending in a body is
    /// still the answer. Folding the flag into the code would make this one a stale-client failure
    /// with no retry, which is the query failing where it used to succeed.
    /// </summary>
    [Test]
    public async Task ARequiresBodyRefusalIsRetriedEvenWhenItIsAlsoAttributedToDrift()
    {
        List<HttpMethod> methods = [];
        var client = StubbedClient(
            request =>
            {
                methods.Add(request.Method);
                if (request.Method == HttpMethod.Get)
                {
                    return Failure(HttpStatusCode.BadRequest, "send it in a body", ScryErrorCode.StaleClient, requiresBody: true);
                }

                return Scalar(3);
            });

        await Assert.That(await Count(client)).IsEqualTo(3);
        await Assert.That(methods).IsEquivalentTo([HttpMethod.Get, HttpMethod.Post], CollectionOrdering.Matching);
    }

    /// <summary>
    /// A code from a server newer than this client degrades to the status and the raw body rather
    /// than to a parse failure. Enum names are read exactly, so the body does not deserialize and it
    /// reads as the "not one of ours" case — which is the right answer either way: the client cannot
    /// act on a code it does not have, and <c>Body</c> still carries everything that was said.
    /// </summary>
    [Test]
    public async Task ACodeThisClientDoesNotKnowReadsAsUnknown()
    {
        var client = StubbedClient(
            _ => new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"something new","code":"RateLimited"}""")
            });

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(() => Count(client)))!;
        using (Assert.Multiple())
        {
            await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.Unknown);
            await Assert.That(exception.Body).Contains("something new");
        }
    }

    // The batch reports an entry exactly as the same query sent alone would, codes included.
    [Test]
    public async Task ARejectedBatchEntryCarriesTheCodeItWouldHaveAlone()
    {
        await using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        var rejected = client.Source<Employee>("Missing")
            .InBatch(batch)
            .CountAsync();

        await batch.SendAsync();

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(async () => await rejected))!;
        await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.Validation);
    }

    static ScryClient BatchingClientFor(TestContext context) =>
        new(
            (request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)),
            batchTransport: (request, _) => Task.FromResult(SharedProcessor.Instance.ExecuteBatch(request, context)));

    static Task<int> Count(ScryClient client) =>
        client.Source<Employee>("Employee", ["Name"]).CountAsync();

    static HttpResponseMessage Failure(
        HttpStatusCode status,
        string message,
        ScryErrorCode code,
        bool requiresBody = false) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new ScryError(message)
                    {
                        Code = code,
                        RequiresBody = requiresBody
                    },
                    ScryJson.Options))
        };

    static HttpResponseMessage Scalar(int value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ScryJson.Serialize(
                    QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(value))))
        };

    static ScryClient StubbedClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new("http://localhost")
        };

        return ScryClient.ForHttp(http, "/api/query");
    }

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel) =>
            Task.FromResult(respond(request));
    }
}
