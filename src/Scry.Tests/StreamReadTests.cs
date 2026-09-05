using System.Net;
using System.Text;

/// <summary>
/// What the client makes of a streamed response's own framing: the opening marker's wire version,
/// and a stream that ends inside a line. Both are the wire's failures to report, not the JSON
/// reader's.
/// </summary>
[TestFixture]
public class StreamReadTests
{
    // The single and batch paths refuse a newer version; the stream's opening marker carried one that
    // was never compared, so a client read every row against a newer encoding.
    [Test]
    public void RefusesANewerWireVersionOnTheOpeningMarker()
    {
        var client = Streaming(
            $$"""{"$scry":"begin","version":{{WireFormat.Version + 1}},"stamp":"s"}""" + "\n" +
            """{"$scry":"end"}""" + "\n");

        var exception = Assert.ThrowsAsync<ScryWireException>(() => Drain(client));

        Assert.That(exception!.Message, Does.Contain("Unsupported response wire version"));
    }

    [Test]
    public async Task ReadsACurrentStream()
    {
        var client = Streaming(
            """{"$scry":"begin","version":1,"stamp":"s"}""" + "\n" +
            """{"name":"Alice"}""" + "\n" +
            """{"$scry":"end"}""" + "\n");

        Assert.That(await Drain(client), Is.EqualTo(1));
    }

    static async Task<int> Drain(ScryClient client)
    {
        var count = 0;
        await foreach (var _ in client.Source<NameOnly>("Employee", ["Name"]).ToAsyncEnumerable())
        {
            count++;
        }

        return count;
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
