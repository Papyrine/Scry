/// <summary>
/// What a client does once a GET has been refused: answered 405, or refused by a hop in front of the
/// server for its length. The fallback is meant to happen once for the life of the client, and the
/// budget every response advertises must not re-open it.
/// </summary>
public class UrlFallbackTests
{
    // A gateway in front of a normally configured server blocks GET; the server's own responses still
    // advertise a budget. The budget once restored the URL form after every fallback, so every query
    // cost a GET, a 405, and a POST.
    [Test]
    public async Task A405IsRememberedPastTheAdvertisedBudget()
    {
        var methods = new List<HttpMethod>();
        var client = Stubbed(request =>
        {
            methods.Add(request.Method);
            var response = request.Method == HttpMethod.Get
                ? new(HttpStatusCode.MethodNotAllowed)
                : Scalar(3);
            response.Headers.TryAddWithoutValidation(WireFormat.UrlLimitHeader, "4096");
            return response;
        });

        await client.Source<NameOnly>("Employee", ["Name"]).CountAsync();
        await client.Source<NameOnly>("Employee", ["Name"]).CountAsync();

        await Assert.That(methods).IsEquivalentTo([HttpMethod.Get, HttpMethod.Post, HttpMethod.Post], CollectionOrdering.Matching);
    }

    // IIS's request filtering refuses a query string past 2048 characters with a 404 of its own, which
    // carries none of Scry's headers, while the server behind it advertises the default budget. The
    // refused query is re-asked as a body; one as long goes straight to a body after it, whatever the
    // server keeps advertising; and a short one is still asked as a URL, so it keeps its caching.
    [Test]
    public async Task AUrlRefusedInFrontOfTheServerIsReaskedAsABodyAndRemembered()
    {
        var methods = new List<HttpMethod>();
        var refused = 0;
        var client = Stubbed(_ =>
        {
            methods.Add(_.Method);
            var query = _.RequestUri!.Query;
            if (_.Method == HttpMethod.Get &&
                query.Length > 1000)
            {
                refused = query.Length - $"?{QueryUrl.Parameter}=".Length;
                return new(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("<html><body><h2>404 - File or directory not found.</h2></body></html>", Encoding.UTF8, "text/html")
                };
            }

            var response = Scalar(3);
            response.Headers.TryAddWithoutValidation(WireFormat.UrlLimitHeader, "4096");
            return response;
        });
        var name = new string('x', 1200);

        await client.Source<NameOnly>("Employee", ["Name"]).Where(_ => _.Name == name).CountAsync();
        await client.Source<NameOnly>("Employee", ["Name"]).Where(_ => _.Name == name).CountAsync();
        await client.Source<NameOnly>("Employee", ["Name"]).Where(_ => _.Name == "Alice").CountAsync();

        await Assert.That(methods).IsEquivalentTo([HttpMethod.Get, HttpMethod.Post, HttpMethod.Post, HttpMethod.Get], CollectionOrdering.Matching);
        await Assert.That(client.QueryUrlLimit).IsEqualTo(refused - 1);
    }

    // What the server itself refused carries its headers, and what authentication in front of it
    // refused would be refused the same way as a body; neither is asked again
    [Test]
    [Arguments(HttpStatusCode.NotFound, true)]
    [Arguments(HttpStatusCode.BadRequest, true)]
    [Arguments(HttpStatusCode.Unauthorized, false)]
    [Arguments(HttpStatusCode.Forbidden, false)]
    public async Task ARefusalABodyWouldNotGetPastIsNotReasked(HttpStatusCode status, bool fromScry)
    {
        var methods = new List<HttpMethod>();
        var client = Stubbed(_ =>
        {
            methods.Add(_.Method);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(ScryJson.Serialize(new ScryError("Refused")), Encoding.UTF8, "application/json")
            };
            if (fromScry)
            {
                response.Headers.TryAddWithoutValidation(WireFormat.UrlLimitHeader, "4096");
            }

            return response;
        });

        await Assert.ThrowsAsync<Exception>(() => client.Source<NameOnly>("Employee", ["Name"]).CountAsync());

        await Assert.That(methods).IsEquivalentTo([HttpMethod.Get], CollectionOrdering.Matching);
        await Assert.That(client.QueryUrlLimit).IsEqualTo(QueryUrl.MaxLength);
    }

    // The pre-encoding length check agrees with the encoding it stands in for at the boundary, so
    // nothing that would have fitted is refused unencoded and nothing past the budget is encoded.
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(3071)]
    [Arguments(3072)]
    [Arguments(3073)]
    public async Task CouldFitAgreesWithTheEncoding(int length)
    {
        var encoded = QueryUrl.Encode(new byte[length]);

        await Assert.That(QueryUrl.CouldFit(length, QueryUrl.MaxLength)).IsEqualTo(QueryUrl.WithinLimit(encoded, QueryUrl.MaxLength));
    }

    public class NameOnly
    {
        public string Name { get; set; } = "";
    }

    static ScryClient Streaming(string body) =>
        Stubbed(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, ScryStream.ContentType)
        });

    static ScryClient Stubbed(Func<HttpRequestMessage, HttpResponseMessage> respond)
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
}
