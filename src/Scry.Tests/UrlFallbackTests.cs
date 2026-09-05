using System.Net;
using System.Text;

/// <summary>
/// What a client does once a GET has been answered 405. The fallback is meant to happen once for the
/// life of the client, and the budget every response advertises must not re-open it.
/// </summary>
[TestFixture]
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
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                : Scalar(3);
            response.Headers.TryAddWithoutValidation(WireFormat.UrlLimitHeader, "4096");
            return response;
        });

        await client.Source<NameOnly>("Employee", ["Name"]).CountAsync();
        await client.Source<NameOnly>("Employee", ["Name"]).CountAsync();

        Assert.That(methods, Is.EqualTo([HttpMethod.Get, HttpMethod.Post, HttpMethod.Post]));
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
