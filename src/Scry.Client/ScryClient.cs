namespace Scry.Client;

/// <summary>
/// The client entry point. Exposes allow-listed sources as <see cref="IQueryable{T}"/> and sends
/// translated queries to the server via a pluggable transport.
/// </summary>
public sealed class ScryClient
{
    readonly Func<QueryRequest, Cancel, Task<QueryResponse>> transport;

    /// <summary>Creates a client over a custom transport.</summary>
    public ScryClient(Func<QueryRequest, Cancel, Task<QueryResponse>> transport) =>
        this.transport = transport;

    // The HTTP transport is an instance method so each response can record the server's advertised
    // schema stamp; a static one could not reach the client it belongs to.
    ScryClient(HttpClient http, string endpoint) =>
        transport = (request, cancel) => PostAsync(http, endpoint, request, cancel);

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

    internal async Task<QueryResponse> SendAsync(QueryRequest request, Cancel cancel)
    {
        var response = await transport(request, cancel);

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

    async Task<QueryResponse> PostAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        Cancel cancel)
    {
        var json = ScryJson.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = await http.PostAsync(endpoint, content, cancel);

        // Recorded from failures too: a rejection caused by schema drift is exactly when this matters.
        if (message.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values))
        {
            RecordServerStamp(values.FirstOrDefault());
        }

        var body = await message.Content.ReadAsStringAsync(cancel);
        if (message.IsSuccessStatusCode)
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

        throw new ScryRequestException((int) message.StatusCode, body);
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
