namespace Scry;

/// <summary>
/// The client entry point. Exposes allow-listed sources as <see cref="IQueryable{T}"/> and sends
/// translated queries to the server via a pluggable transport.
/// </summary>
public sealed class ScryClient
{
    readonly Func<QueryRequest, ScryCall?, Cancel, Task<QueryResponse>> transport;
    readonly Func<QueryRequest, ScryCall?, Cancel, IAsyncEnumerable<JsonElement>>? streamTransport;
    readonly Func<QueryBatchRequest, Cancel, Task<QueryBatchResponse>>? batchTransport;

    /// <summary>
    /// Creates a client over a custom transport. <paramref name="streamTransport"/> and
    /// <paramref name="batchTransport"/> are optional: a transport that cannot stream simply has no
    /// <c>ToAsyncEnumerable</c>, and one that cannot batch has no <see cref="Batch"/> — each says so
    /// rather than quietly buffering the whole result, or sending a "batch" one query at a time.
    /// </summary>
    /// <remarks>
    /// Per-query headers are HTTP's, so a transport supplied here does not receive them and a query
    /// carrying them is refused rather than sent without them. Use <see cref="ForHttp"/> for those.
    /// </remarks>
    public ScryClient(
        Func<QueryRequest, Cancel, Task<QueryResponse>> transport,
        Func<QueryRequest, Cancel, IAsyncEnumerable<JsonElement>>? streamTransport = null,
        Func<QueryBatchRequest, Cancel, Task<QueryBatchResponse>>? batchTransport = null)
    {
        this.batchTransport = batchTransport;

        this.transport = (request, call, cancel) =>
        {
            RefuseHeaders(call);
            return transport(request, cancel);
        };

        this.streamTransport = streamTransport is null
            ? null
            : (request, call, cancel) =>
            {
                RefuseHeaders(call);
                return streamTransport(request, cancel);
            };
    }

    // The HTTP transport is an instance method so each response can record the server's advertised
    // schema stamp; a static one could not reach the client it belongs to.
    ScryClient(HttpClient http, string endpoint)
    {
        transport = (request, call, cancel) => PostAsync(http, endpoint, request, call, cancel);
        streamTransport = (request, call, cancel) => StreamAsync(http, $"{endpoint.TrimEnd('/')}/stream", request, call, cancel);
        batchTransport = (request, cancel) => PostBatchAsync(http, $"{endpoint.TrimEnd('/')}/batch", request, cancel);
    }

    // A custom transport has nowhere to put a header, so a query that asked for one cannot be honoured.
    // Refusing keeps WithHeader from looking like it worked on a client that never sent it.
    static void RefuseHeaders(ScryCall? call)
    {
        if (call is null)
        {
            return;
        }

        throw new NotSupportedException(
            "Per-query headers require the HTTP transport. Build the client with ScryClient.ForHttp, or drop " +
            "WithHeader/WithHeaders/OnResponseHeaders from the query.");
    }

    // begin-snippet: scryClientApi
    /// <summary>Creates a client that POSTs queries to an HTTP endpoint.</summary>
    public static ScryClient ForHttp(HttpClient http, string endpoint) =>
        new(http, endpoint);

    /// <summary>
    /// Returns an <see cref="IQueryable{T}"/> backed by the named allow-listed source.
    /// <paramref name="defaultProjection"/> is the source's scalar member names, passed by the
    /// generated entry point so a query without a <c>Select</c> still projects explicitly.
    /// </summary>
    public IQueryable<T> Source<T>(string name, IReadOnlyList<string>? defaultProjection = null) =>
        new CaptureQueryable<T>(new(this, name, defaultProjection));

    /// <summary>
    /// Starts a batch: several queries collected on the client and sent as one request. Attach it to a
    /// query with <see cref="ScryBatchExtensions.InBatch{T}"/>, then
    /// <see cref="ScryBatch.SendAsync"/>. A batch is used once.
    /// </summary>
    public ScryBatch Batch()
    {
        if (batchTransport is null)
        {
            throw new NotSupportedException(
                "This client's transport does not batch. Send queries individually, or construct the client " +
                "with a batch transport (ScryClient.ForHttp does).");
        }

        return new(this);
    }
    // end-snippet

    /// <summary>
    /// The schema stamp of the generated model this client queries with. Assigned by the generated
    /// <c>ScryQuery</c> entry point and attached to each request, so the server can identify a client
    /// generated against a different model. Null (e.g. for hand-built sources) sends no stamp.
    /// </summary>
    public string? SchemaStamp { get; set; }

    /// <summary>
    /// The schema stamp the server advertised on the most recent response, or null before the first
    /// one. Carried in the response body over any transport, and additionally as a header over HTTP
    /// (which also covers error responses). Compare with <see cref="SchemaStamp"/> to detect
    /// a drifted model while queries are still succeeding — a long-lived client (a cached WASM app)
    /// can use a difference to prompt a reload before a breaking change reaches it.
    /// </summary>
    public string? ServerSchemaStamp { get; private set; }

    /// <summary>
    /// True once the server has advertised a schema stamp that differs from this client's, meaning the
    /// client was generated against a different model surface. Queries may still succeed — an additive
    /// model change breaks nothing — so treat this as a signal to regenerate or reload, not an error.
    /// </summary>
    public bool SchemaStale =>
        SchemaStamp is { } client &&
        ServerSchemaStamp is { } server &&
        client != server;

    /// <summary>
    /// Raised the first time a response reveals that the server's queryable surface differs from the
    /// one this client was generated against — typically because the server was redeployed while a
    /// cached client (a WASM app left open in a tab) kept running. Handle it to prompt a reload.
    /// </summary>
    /// <remarks>
    /// Raised at most once per client, on the thread that awaited the query, and after the response
    /// has been recorded — so <see cref="SchemaStale"/> is already true when the handler runs. Drift
    /// is not an error: the query that revealed it has still succeeded (or failed on its own merits),
    /// and an additive model change leaves an older client working indefinitely.
    /// </remarks>
    public event Action<SchemaDrift>? SchemaStaleDetected;

    bool staleRaised;

    internal async Task<QueryResponse> SendAsync(QueryRequest request, ScryCall? call, Cancel cancel)
    {
        var response = await transport(request, call, cancel);

        // Every successful response carries the server's stamp, so drift detection works over any
        // transport rather than only HTTP. The HTTP transport has already recorded the same value from
        // the response header — which it keeps, since a header also rides on error responses, where
        // there is no body to read it from. A response without a stamp records nothing rather than
        // clearing what the header found.
        if (response.Stamp is { } stamp)
        {
            RecordServerStamp(stamp);
        }

        return response;
    }

    internal async Task<QueryBatchResponse> SendBatchAsync(QueryBatchRequest request, Cancel cancel)
    {
        var response = await batchTransport!(request, cancel);

        // Carried once for the whole batch rather than per entry: one server answered all of them.
        if (response.Stamp is { } stamp)
        {
            RecordServerStamp(stamp);
        }

        return response;
    }

    // Raised once, not per response: a chatty app would otherwise re-prompt on every query for as
    // long as the drift lasts.
    void RecordServerStamp(string? server)
    {
        ServerSchemaStamp = server;
        if (staleRaised ||
            !SchemaStale)
        {
            return;
        }

        staleRaised = true;
        SchemaStaleDetected?.Invoke(new(SchemaStamp!, ServerSchemaStamp!));
    }

    internal IAsyncEnumerable<JsonElement> StreamAsync(QueryRequest request, ScryCall? call, Cancel cancel)
    {
        if (streamTransport is not { } stream)
        {
            throw new NotSupportedException(
                "This client's transport does not stream. Use ToListAsync, or construct the client with a " +
                "stream transport (ScryClient.ForHttp does).");
        }

        return stream(request, call, cancel);
    }

    /// <summary>
    /// Reads a newline-delimited response, yielding one row per line. The stream's own markers are
    /// consumed here rather than surfaced: the opening one records the server's stamp and its enum
    /// aliases, and the closing one decides whether the rows that arrived are the whole result.
    /// </summary>
    async IAsyncEnumerable<JsonElement> StreamAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        ScryCall? call,
        [EnumeratorCancellation] Cancel cancel)
    {
        var json = ScryJson.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) {Content = content};
        call?.Configure(message.Headers);

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel);

        if (response.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values))
        {
            RecordServerStamp(values.FirstOrDefault());
        }

        // Read here rather than after the rows: this is the point the response headers are known, and
        // every path below it either throws or hands control to the caller mid-enumeration.
        call?.Read(response.Headers);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancel);
            if (TryParseError(body) is { StaleClient: true, Error.Length: > 0 } error)
            {
                throw new ScryStaleClientException(error.Error);
            }

            throw new ScryRequestException((int) response.StatusCode, body);
        }

        await using var lines = await response.Content.ReadAsStreamAsync(cancel);
        using var reader = new StreamReader(lines);

        var ended = false;
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var element = JsonSerializer.Deserialize<JsonElement>(line, ScryJson.Options);
            if (!element.TryGetProperty(ScryStream.MarkerProperty, out var kind))
            {
                yield return element;
                continue;
            }

            var marker = element.Deserialize<ScryStreamMarker>(ScryJson.Options)!;
            switch (kind.GetString())
            {
                case ScryStream.Begin:
                    if (marker.Stamp is { } stamp)
                    {
                        RecordServerStamp(stamp);
                    }

                    StreamAliases = marker.EnumAliases;
                    break;

                case ScryStream.End:
                    ended = true;
                    break;

                case ScryStream.Error:
                    throw new ScryWireException(
                        $"The server ended the stream early: {marker.Error ?? "no reason given"}");
            }
        }

        // Absent closing marker: the connection ended before the server said it was finished, so the
        // rows that arrived are a prefix of the answer rather than the answer.
        if (!ended)
        {
            throw new ScryWireException(
                "The result stream ended without its closing marker, so the rows received are incomplete.");
        }
    }

    /// <summary>
    /// The enum aliases the current stream opened with, so a row read mid-stream resolves a renamed
    /// value the same way a single response's payload does.
    /// </summary>
    internal IReadOnlyList<EnumAlias>? StreamAliases { get; private set; }

    async Task<QueryResponse> PostAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        ScryCall? call,
        Cancel cancel)
    {
        var json = ScryJson.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Built explicitly rather than posted through HttpClient.PostAsync: a per-query header needs a
        // request message of its own to be written onto.
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) {Content = content};
        call?.Configure(message.Headers);

        using var response = await http.SendAsync(message, cancel);

        // Recorded from failures too: a rejection caused by schema drift is exactly when this matters.
        if (response.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values))
        {
            RecordServerStamp(values.FirstOrDefault());
        }

        // Read before the body is inspected, so the hook still runs on the paths below that throw.
        call?.Read(response.Headers);

        var body = await response.Content.ReadAsStringAsync(cancel);
        if (response.IsSuccessStatusCode)
        {
            return ScryJson.DeserializeResponse(body);
        }

        // A failure the server attributed to this client's schema stamp surfaces as the same exception
        // the payload reader throws for an unknown enum value, so one catch covers every stale-client
        // failure and can prompt a reload. SchemaStaleDetected has already been raised above.
        if (TryParseError(body) is { StaleClient: true, Error.Length: > 0 } error)
        {
            throw new ScryStaleClientException(error.Error);
        }

        throw new ScryRequestException((int) response.StatusCode, body);
    }

    /// <summary>
    /// Posts a batch. A non-success status here is a failure of the batch itself — an unreadable body,
    /// or a rejection of the whole envelope; a rejected entry rides inside a successful response and is
    /// raised on that entry's own task instead.
    /// </summary>
    async Task<QueryBatchResponse> PostBatchAsync(
        HttpClient http,
        string endpoint,
        QueryBatchRequest request,
        Cancel cancel)
    {
        var json = ScryJson.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(endpoint, content, cancel);

        if (response.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values))
        {
            RecordServerStamp(values.FirstOrDefault());
        }

        var body = await response.Content.ReadAsStringAsync(cancel);
        if (response.IsSuccessStatusCode)
        {
            return ScryJson.DeserializeBatchResponse(body);
        }

        if (TryParseError(body) is {StaleClient: true, Error.Length: > 0} error)
        {
            throw new ScryStaleClientException(error.Error);
        }

        throw new ScryRequestException((int) response.StatusCode, body);
    }

    // A non-success body is usually the endpoint's ScryError, but may be anything once proxies or
    // other middleware are involved — an unparseable body falls back to the raw-bodied exception.
    static ScryError? TryParseError([StringSyntax(StringSyntaxAttribute.Json)] string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ScryError>(body, ScryJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
