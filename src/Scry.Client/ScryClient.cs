namespace Scry;

/// <summary>
/// The client entry point. Exposes allow-listed sources as <see cref="IQueryable{T}"/> and sends
/// translated queries to the server via a pluggable transport.
/// </summary>
public sealed class ScryClient
{
    readonly Func<QueryRequest, ScryCall?, Cancel, Task<QueryResponse>> transport;
    readonly Func<QueryRequest, ScryCall?, Cancel, IAsyncEnumerable<StreamedRow>>? streamTransport;
    readonly Func<QueryBatchRequest, Cancel, Task<QueryBatchResponse>>? batchTransport;
    readonly Func<AttachmentRequest, Cancel, Task<Stream?>>? attachmentTransport;

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
                return Adapt(streamTransport(request, cancel), cancel);
            };
    }

    // A supplied transport yields rows already parsed and consumes no markers of its own, so its rows
    // carry neither aliases nor binary parts — exactly what they carried before rows began holding them.
    static async IAsyncEnumerable<StreamedRow> Adapt(
        IAsyncEnumerable<JsonElement> rows,
        [EnumeratorCancellation] Cancel cancel)
    {
        await foreach (var row in rows.WithCancellation(cancel))
        {
            yield return StreamedRow.FromElement(row);
        }
    }

    // The HTTP transport is an instance method so each response can record the server's advertised
    // schema stamp; a static one could not reach the client it belongs to.
    ScryClient(HttpClient http, string endpoint)
    {
        transport = (request, call, cancel) => PostAsync(http, endpoint, request, call, cancel);
        streamTransport = (request, call, cancel) => StreamAsync(http, $"{endpoint.TrimEnd('/')}/stream", request, call, cancel);
        batchTransport = (request, cancel) => PostBatchAsync(http, $"{endpoint.TrimEnd('/')}/batch", request, cancel);
        attachmentTransport = (request, cancel) => PostAttachmentAsync(http, $"{endpoint.TrimEnd('/')}/attachment", request, cancel);
    }

    // Sends the serializer's own UTF-8 rather than a string: StringContent would encode the body to UTF-8
    // at send time, so a string in between costs a transcode each way and an allocation discarded straight
    // after. The content type is written out explicitly to stay byte-identical to what StringContent set.
    static ByteArrayContent JsonBody(byte[] utf8)
    {
        var content = new ByteArrayContent(utf8);
        content.Headers.ContentType = new("application/json")
        {
            CharSet = "utf-8"
        };
        // Fingerprints exactly what is being sent, which is free here — the bytes are in hand and about to
        // be handed to the socket. Carried on the content rather than the message because it describes the
        // entity, and because every sender below builds content where only some build a message of their
        // own. The server treats it as advisory; see QueryFingerprint.
        content.Headers.TryAddWithoutValidation(WireFormat.QueryHashHeader, QueryFingerprint.Compute(utf8));
        return content;
    }

    // base64url carries nothing a query string would have to escape, so the encoded request is appended
    // as it stands. An endpoint that already carries parameters of its own keeps them.
    static string Url(string endpoint, string encoded) =>
        endpoint.Contains('?')
            ? $"{endpoint}&{QueryUrl.Parameter}={encoded}"
            : $"{endpoint}?{QueryUrl.Parameter}={encoded}";

    // A custom transport has nowhere to put a header, so a query that asked for one cannot be honoured.
    // Refusing keeps WithHeader from looking like it worked on a client that never sent it.
    static void RefuseHeaders(ScryCall? call)
    {
        if (call is null)
        {
            return;
        }

        throw new NotSupportedException(
            """
            Per-query headers require the HTTP transport.
            Build the client with ScryClient.ForHttp, or drop WithHeader/WithHeaders/OnResponseHeaders from the query.
            """);
    }

    // begin-snippet: scryClientApi
    /// <summary>
    /// Creates a client that sends queries to an HTTP endpoint — as a URL where the query fits in one
    /// and as a body where it does not, which <see cref="QueryUrl"/> explains.
    /// </summary>
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
        if (batchTransport is not null)
        {
            return new(this);
        }

        throw new NotSupportedException(
            """
            This client's transport does not batch.
            Send queries individually, or construct the client with a batch transport (ScryClient.ForHttp does).
            """);
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
        // the response header — which it keeps, since a header is also present on error responses, where
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

    internal IAsyncEnumerable<StreamedRow> StreamAsync(QueryRequest request, ScryCall? call, Cancel cancel)
    {
        if (streamTransport is { } stream)
        {
            return stream(request, call, cancel);
        }

        throw new NotSupportedException(
            """
            This client's transport does not stream.
            Use ToListAsync, or construct the client with a stream transport (ScryClient.ForHttp does).
            """);
    }

    /// <summary>
    /// Reads a newline-delimited response, yielding one row per line. The stream's own markers are
    /// consumed here rather than surfaced: the opening one records the server's stamp and its enum
    /// aliases, and the closing one decides whether the rows that arrived are the whole result.
    /// </summary>
    async IAsyncEnumerable<StreamedRow> StreamAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        ScryCall? call,
        [EnumeratorCancellation] Cancel cancel)
    {
        using var content = JsonBody(ScryJson.SerializeToUtf8(request));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
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
            var body = await response.Content.ReadAsByteArrayAsync(cancel);
            if (ScryJson.TryDeserializeError(body) is { StaleClient: true, Error.Length: > 0 } error)
            {
                throw new ScryStaleClientException(error.Error);
            }

            throw new ScryRequestException((int) response.StatusCode, Encoding.UTF8.GetString(body));
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancel);

        var ended = false;
        IReadOnlyList<EnumAlias>? aliases = null;

        // A multipart stream alternates ndjson-line sections with sections carrying the next row's
        // binary parts; a plain stream is one run of lines. The parts accumulated since the last row
        // line belong to the next one, and to it only — so the reader holds at most one row's parts.
        if (MultipartResponse.TryGetBoundary(response, out var boundary))
        {
            var multipart = new MultipartReader(boundary, responseStream);
            var pending = new List<byte[]>();
            while (await multipart.ReadNextSectionAsync(cancel) is { } section)
            {
                if (MultipartResponse.IsBinary(section))
                {
                    pending.Add(await MultipartResponse.ReadPartBytes(section, cancel));
                    continue;
                }

                using var lines = new NdjsonReader(section.Body);
                while (await lines.ReadLineAsync(cancel) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (!IsMarker(line.Span))
                    {
                        yield return StreamedRow.FromUtf8(line) with
                        {
                            Aliases = aliases,
                            Parts = pending.Count > 0 ? pending.ToArray() : null
                        };
                        pending.Clear();
                        continue;
                    }

                    (var closed, aliases) = HandleMarker(line.Span, aliases);
                    ended |= closed;
                }
            }
        }
        else
        {
            using var reader = new NdjsonReader(responseStream);
            while (await reader.ReadLineAsync(cancel) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (!IsMarker(line.Span))
                {
                    yield return StreamedRow.FromUtf8(line) with {Aliases = aliases};
                    continue;
                }

                (var closed, aliases) = HandleMarker(line.Span, aliases);
                ended |= closed;
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
    /// Whether a line is one of the stream's own markers rather than a row. Reads only far enough to
    /// answer — a row is not parsed here, since the caller materializes it into its own type.
    /// </summary>
    static bool IsMarker(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() ||
            reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read() &&
               reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(ScryStream.MarkerProperty))
            {
                return true;
            }

            reader.Read();
            reader.Skip();
        }

        return false;
    }

    // The stream's own markers are consumed rather than surfaced: the opening one records the server's
    // stamp and its enum aliases, and the closing one decides whether the rows are the whole result.
    // Returns whether the marker closes the stream, and the aliases in force after it.
    (bool Ended, IReadOnlyList<EnumAlias>? Aliases) HandleMarker(
        ReadOnlySpan<byte> line,
        IReadOnlyList<EnumAlias>? aliases)
    {
        var marker = ScryJson.DeserializeMarker(line);
        switch (marker.Kind)
        {
            case ScryStream.Begin:
                if (marker.Stamp is { } stamp)
                {
                    RecordServerStamp(stamp);
                }

                return (false, marker.EnumAliases);

            case ScryStream.End:
                return (true, aliases);

            case ScryStream.Error:
                throw new ScryWireException(
                    $"The server ended the stream early: {marker.Error ?? "no reason given"}");
        }

        return (false, aliases);
    }

    async Task<QueryResponse> PostAsync(
        HttpClient http,
        string endpoint,
        QueryRequest request,
        ScryCall? call,
        Cancel cancel)
    {
        var utf8 = ScryJson.SerializeToUtf8(request);

        // Asked as a URL whenever it fits in one. A POST is uncacheable by everything between here and
        // the server, so every repeat of a query costs a full round trip and a full response; the same
        // query as a GET is stored and revalidated by the caller's own HTTP cache, which is machinery
        // that already exists and needs no client code. What does not fit stays a POST — see QueryUrl
        // for the length that bounds this, and for what a URL exposes that a body does not.
        var encoded = QueryUrl.Encode(utf8);
        using var content = QueryUrl.WithinLimit(encoded) ? null : JsonBody(utf8);

        // Built explicitly rather than sent through HttpClient.PostAsync/GetAsync: a per-query header
        // needs a request message of its own to be written onto.
        using var message = content is null
            ? new HttpRequestMessage(HttpMethod.Get, Url(endpoint, encoded))
            : new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };

        // A GET has no content to carry the fingerprint, and the URL already identifies the request as
        // exactly as well. Sent anyway, on the message, so a server reading it for telemetry sees the
        // same value whichever way the query was asked.
        if (content is null)
        {
            message.Headers.TryAddWithoutValidation(WireFormat.QueryHashHeader, QueryFingerprint.Compute(utf8));
        }

        call?.Configure(message.Headers);

        using var response = await http.SendAsync(message, cancel);

        // Recorded from failures too: a rejection caused by schema drift is exactly when this matters.
        if (response.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values))
        {
            RecordServerStamp(values.FirstOrDefault());
        }

        // Read before the body is inspected, so the hook still runs on the paths below that throw.
        call?.Read(response.Headers);

        // A multipart response carries [BinaryTransfer] values as raw parts before its JSON envelope;
        // the parts ride the response object for the payload reader to resolve placeholders against.
        if (response.IsSuccessStatusCode &&
            MultipartResponse.TryGetBoundary(response, out var boundary))
        {
            var (envelope, parts) = await MultipartResponse.ReadAsync(response, boundary, cancel);
            return ScryJson.DeserializeResponse(envelope) with {BinaryParts = parts};
        }

        // Read as the UTF-8 it arrived as: the JSON reader wants those bytes, so decoding to a string
        // first transcodes the whole response to UTF-16 only for the reader to transcode it back. The
        // bytes are also what the response keeps, so the payload is parsed once, straight into the
        // caller's type. Only a failure below needs text, and one is small.
        var body = await response.Content.ReadAsByteArrayAsync(cancel);
        if (response.IsSuccessStatusCode)
        {
            return ScryJson.DeserializeResponse(body);
        }

        // A failure the server attributed to this client's schema stamp surfaces as the same exception
        // the payload reader throws for an unknown enum value, so one catch covers every stale-client
        // failure and can prompt a reload. SchemaStaleDetected has already been raised above.
        if (ScryJson.TryDeserializeError(body) is { StaleClient: true, Error.Length: > 0 } error)
        {
            throw new ScryStaleClientException(error.Error);
        }

        throw new ScryRequestException((int) response.StatusCode, Encoding.UTF8.GetString(body));
    }

    // Reached from ScryAttachment.OpenAsync, which a materialized row hands out; the transport is the
    // client's, so a handle cannot outlive knowing where to fetch from.
    internal Task<Stream?> OpenAttachmentAsync(AttachmentRequest request, Cancel cancel)
    {
        if (attachmentTransport is not null)
        {
            return attachmentTransport(request, cancel);
        }

        throw new NotSupportedException(
            """
            This client's transport does not fetch attachments.
            Construct the client with ScryClient.ForHttp, which maps the attachment endpoint alongside the query one.
            """);
    }

    /// <summary>
    /// Fetches one attachment's bytes. The response is read headers-first and handed back unbuffered,
    /// so a large value streams rather than materializing; the returned stream owns the response and
    /// releases it when disposed.
    /// </summary>
    async Task<Stream?> PostAttachmentAsync(
        HttpClient http,
        string endpoint,
        AttachmentRequest request,
        Cancel cancel)
    {
        using var content = JsonBody(ScryJson.SerializeToUtf8(request));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };

        var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel);
        var disposeResponse = true;
        try
        {
            // Recorded from failures too, exactly as a query response is: a 404 caused by drift is
            // where a stale client most wants to know.
            if (response.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values))
            {
                RecordServerStamp(values.FirstOrDefault());
            }

            // A null value produces no body — the row was readable, the column simply holds nothing.
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return null;
            }

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStreamAsync(cancel);
                disposeResponse = false;
                return new AttachmentStream(body, response);
            }

            var error = await response.Content.ReadAsByteArrayAsync(cancel);
            if (ScryJson.TryDeserializeError(error) is {StaleClient: true, Error.Length: > 0} stale)
            {
                throw new ScryStaleClientException(stale.Error);
            }

            throw new ScryRequestException((int) response.StatusCode, Encoding.UTF8.GetString(error));
        }
        finally
        {
            if (disposeResponse)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Posts a batch. A non-success status here is a failure of the batch itself — an unreadable body,
    /// or a rejection of the whole envelope; a rejected entry is returned inside a successful response and is
    /// raised on that entry's own task instead.
    /// </summary>
    async Task<QueryBatchResponse> PostBatchAsync(
        HttpClient http,
        string endpoint,
        QueryBatchRequest request,
        Cancel cancel)
    {
        using var content = JsonBody(ScryJson.SerializeToUtf8(request));
        using var response = await http.PostAsync(endpoint, content, cancel);

        if (response.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values))
        {
            RecordServerStamp(values.FirstOrDefault());
        }

        // A batch's parts are numbered globally across entries, so the one list serves every result.
        if (response.IsSuccessStatusCode &&
            MultipartResponse.TryGetBoundary(response, out var boundary))
        {
            var (envelope, parts) = await MultipartResponse.ReadAsync(response, boundary, cancel);
            return ScryJson.DeserializeBatchResponse(envelope) with {BinaryParts = parts};
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancel);
        if (response.IsSuccessStatusCode)
        {
            return ScryJson.DeserializeBatchResponse(body);
        }

        if (ScryJson.TryDeserializeError(body) is {StaleClient: true, Error.Length: > 0} error)
        {
            throw new ScryStaleClientException(error.Error);
        }

        throw new ScryRequestException((int) response.StatusCode, Encoding.UTF8.GetString(body));
    }
}
