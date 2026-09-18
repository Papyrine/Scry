using System.Net.ServerSentEvents;

namespace Scry;

public sealed partial class ScryClient
{
    /// <summary>
    /// When a live query whose connection ended asks again, and whether it does. By default: at once,
    /// then after a second, doubling to thirty, for as long as it takes — a live query that gave up
    /// would be a page that went stale without saying so. Set one that returns null to give up.
    /// </summary>
    public IScryRetryPolicy Reconnect { get; set; } = BackoffRetryPolicy.Instance;

    /// <summary>
    /// One connection's worth of a live query: its answers until the connection ends. Asking again
    /// when it does is <see cref="LivePump"/>'s, which is what every consumer goes through.
    /// </summary>
    internal IAsyncEnumerable<LiveFrame> LiveAsync(QueryRequest request, ScryCall? call, string? lastEventId, Cancel cancel)
    {
        if (liveTransport is { } live)
        {
            return live(request, call, lastEventId, cancel);
        }

        throw new NotSupportedException(
            """
            This client's transport cannot hold a query open.
            Construct the client with a subscribe transport (ScryClient.ForHttp does), or ask once with ToListAsync.
            """);
    }

    // A supplied transport yields whole responses and has no names for them, so nothing can be sent
    // back when asking again; the pump compares the first answer of each connection instead. Running
    // out is the transport's way of saying the server ended it, which is asked for again.
    static async IAsyncEnumerable<LiveFrame> Adapt(
        IAsyncEnumerable<QueryResponse> answers,
        [EnumeratorCancellation] Cancel cancel)
    {
        await foreach (var answer in answers.WithCancellation(cancel))
        {
            yield return LiveFrame.Result(answer, id: null);
        }
    }

    /// <summary>
    /// Reads a live query as server-sent events. Each <c>result</c> is a response read exactly as the
    /// query endpoint's is, so everything downstream of here — aliases, drift, materialization — is
    /// the path a query asked once takes.
    /// </summary>
    async IAsyncEnumerable<LiveFrame> LiveAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        ScryCall? call,
        string? lastEventId,
        [EnumeratorCancellation] Cancel cancel)
    {
        using var content = JsonBody(ScryJson.SerializeToUtf8(request));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        message.Headers.Accept.Add(new(ScryLive.ContentType));
        if (lastEventId is not null)
        {
            message.Headers.TryAddWithoutValidation(ScryLive.LastEventIdHeader, lastEventId);
        }

        // In a browser the response is otherwise handed over only once it is complete, which for this
        // one is never. Set by name: the typed extension lives in the WebAssembly package, which this
        // one does not reference, and the option means nothing anywhere else.
        message.Options.Set(streamingResponse, true);
        call?.Configure(message.Headers);

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel);

        RecordServerHeaders(response);
        call?.Read(response.Headers);

        // No such route: the server has not said how many live queries it will hold, so it maps none.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            throw NotEnabled();
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancel);
            throw ResponseFailure.Read(response.StatusCode, body);
        }

        // A success that is not an event stream is somebody else's answer — a single-page app's
        // fallback route serving its index page for a path the server does not map, usually.
        if (response.Content.Headers.ContentType?.MediaType != ScryLive.ContentType)
        {
            throw NotEnabled();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancel);

        // The data is copied out as it is parsed: the span is the parser's own buffer, and a response
        // keeps hold of the bytes its payload was read from.
        var events = SseParser.Create(stream, (_, data) => data.ToArray());
        await foreach (var item in events.EnumerateAsync(cancel))
        {
            switch (item.EventType)
            {
                case ScryLive.Result:
                    var answer = ScryJson.DeserializeResponse((ReadOnlyMemory<byte>)item.Data);
                    if (answer.Stamp is { } stamp)
                    {
                        RecordServerStamp(stamp);
                    }

                    yield return LiveFrame.Result(answer, item.EventId);
                    break;

                case ScryLive.Unchanged:
                    yield return LiveFrame.Unchanged;
                    break;

                case ScryLive.Error:
                    throw ResponseFailure.Read(item.Data);

                case ScryLive.End:
                    yield return LiveFrame.End(ScryJson.DeserializeLiveEnd(item.Data).Reconnect);
                    yield break;
            }

            // A heartbeat, or an event from a server newer than this client: neither is an answer.
        }

        // Ran out with no closing event, so the connection was cut rather than ended. The pump asks
        // again, which is all there is to do about it.
    }

    static HttpRequestOptionsKey<bool> streamingResponse = new("WebAssemblyEnableStreamingResponse");

    static NotSupportedException NotEnabled() =>
        new(
            """
            This server is not serving live queries.
            Set ScryOptions.MaxSubscriptions to how many it may hold open at once, which is what maps the route.
            """);
}
