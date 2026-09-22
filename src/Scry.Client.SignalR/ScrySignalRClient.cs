namespace Scry;

/// <summary>
/// A <see cref="ScryClient"/> whose queries travel over a SignalR hub connection rather than HTTP. The
/// server half is <c>MapScryHub</c>, from Scry.Server.SignalR.
/// </summary>
public static class ScrySignalRClient
{
    // begin-snippet: signalRClient
    /// <summary>
    /// Creates a client over <paramref name="connection"/>. Everything written against a
    /// <see cref="ScryClient"/> works unchanged — the terminals, streaming, batching, live queries,
    /// commands — and every live query and pending command shares the one connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection is the caller's: to build, to start, to configure with
    /// <c>WithAutomaticReconnect</c>, and to dispose. While it is down a query fails as any call on a
    /// closed connection does, and a live query asks again under <see cref="ScryClient.Reconnect"/>
    /// until it is back.
    /// </para>
    /// <para>
    /// Per-query headers are HTTP's, so a query carrying them is refused here as it is over any
    /// transport other than <see cref="ScryClient.ForHttp"/>. Attachments are fetched over HTTP.
    /// </para>
    /// </remarks>
    public static ScryClient Create(HubConnection connection) =>
        new(
            (request, cancel) => Query(connection, request, cancel),
            (request, cancel) => Rows(connection, request, cancel),
            (request, cancel) => Batch(connection, request, cancel),
            (request, cancel) => Answers(connection, request, cancel),
            (request, cancel) => Receipts(connection, ScryHubProtocol.Command, ScryJson.Serialize(request), cancel),
            (id, cancel) => Receipts(connection, ScryHubProtocol.Receipt, id.ToString("D"), cancel),
            cancel => Capabilities(connection, cancel));
    // end-snippet

    // A command's receipts, or one it was sent before asked for again by its id. The client asks for a
    // pending one again whenever this ends before its outcome — including while the connection is
    // down, which is said as the connection failure it is rather than as a mistake in the call, so that
    // it is asked again once the connection is back rather than given up on.
    static async IAsyncEnumerable<CommandReceipt> Receipts(
        HubConnection connection,
        string method,
        string argument,
        [EnumeratorCancellation] Cancel cancel)
    {
        using var stopping = CancelSource.CreateLinkedTokenSource(cancel);
        try
        {
            await using var receipts = connection
                .StreamAsync<string>(method, argument, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            while (true)
            {
                try
                {
                    if (!await receipts.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (InvalidOperationException exception) when (connection.State != HubConnectionState.Connected)
                {
                    throw new IOException("The hub connection is not connected.", exception);
                }

                yield return ScryJson.DeserializeReceipt(Answer(receipts.Current));
            }
        }
        finally
        {
            await Stop(stopping);
        }
    }

    static async Task<CommandCapabilities> Capabilities(HubConnection connection, Cancel cancel)
    {
        var answer = await connection.InvokeAsync<string>(ScryHubProtocol.Capabilities, cancel);
        return ScryJson.DeserializeCapabilities(Answer(answer));
    }

    static async Task<QueryResponse> Query(HubConnection connection, QueryRequest request, Cancel cancel)
    {
        var answer = await connection.InvokeAsync<string>(ScryHubProtocol.Query, ScryJson.Serialize(request), cancel);
        return ScryJson.DeserializeResponse((ReadOnlyMemory<byte>)Answer(answer));
    }

    static async Task<QueryBatchResponse> Batch(HubConnection connection, QueryBatchRequest request, Cancel cancel)
    {
        var answer = await connection.InvokeAsync<string>(ScryHubProtocol.Batch, ScryJson.Serialize(request), cancel);
        return ScryJson.DeserializeBatchResponse((ReadOnlyMemory<byte>)Answer(answer));
    }

    // The lines the stream endpoint writes. The markers are consumed here, since a supplied transport
    // hands the client rows and nothing else — and the closing one is required, for the reason it is
    // over HTTP: without it the rows that arrived are a prefix rather than the answer.
    static async IAsyncEnumerable<JsonElement> Rows(
        HubConnection connection,
        QueryRequest request,
        [EnumeratorCancellation] Cancel cancel)
    {
        var ended = false;
        using var stopping = CancelSource.CreateLinkedTokenSource(cancel);
        try
        {
            var lines = connection.StreamAsync<string>(ScryHubProtocol.Stream, ScryJson.Serialize(request), stopping.Token);
            await foreach (var line in lines)
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                if (!ScryJson.IsMarker(bytes))
                {
                    using var document = JsonDocument.Parse(bytes);
                    yield return document.RootElement.Clone();
                    continue;
                }

                var marker = ScryJson.DeserializeMarker(bytes);
                ThrowIfFailure(marker);
                ended |= marker.Kind == ScryStream.End;
            }
        }
        finally
        {
            await Stop(stopping);
        }

        if (!ended)
        {
            throw new ScryWireException(
                "The result stream ended without its closing marker, so the rows received are incomplete.");
        }
    }

    // A sequence that ends is asked for again by the client, and one that throws ends the live query
    // unless the failure is one a later attempt could get past — which is decided from the exception,
    // and so from the code the hub put on its marker.
    static async IAsyncEnumerable<QueryResponse> Answers(
        HubConnection connection,
        QueryRequest request,
        [EnumeratorCancellation] Cancel cancel)
    {
        using var stopping = CancelSource.CreateLinkedTokenSource(cancel);
        try
        {
            var answers = connection.StreamAsync<string>(ScryHubProtocol.Subscribe, ScryJson.Serialize(request), stopping.Token);
            await foreach (var answer in answers)
            {
                yield return ScryJson.DeserializeResponse((ReadOnlyMemory<byte>)Answer(answer));
            }
        }
        finally
        {
            await Stop(stopping);
        }
    }

    // A hub connection stops the server's stream when the token it was given is cancelled, and not
    // when the enumerator is disposed. Left to disposal alone, a consumer that walked away from an
    // await foreach would leave the server running the query for a caller that had gone — and, for a
    // live query, holding one of the places the server allows. So the token is this method's own, and
    // leaving the method by any road cancels it.
    static Task Stop(CancelSource stopping) =>
        stopping.CancelAsync();

    // The bytes of an answer, or the exception for the failure it turned out to be.
    static byte[] Answer(string answer)
    {
        var bytes = Encoding.UTF8.GetBytes(answer);
        if (ScryJson.IsMarker(bytes))
        {
            ThrowIfFailure(ScryJson.DeserializeMarker(bytes));
        }

        return bytes;
    }

    // Surfaced as the same failure answered with a status would have been, so that what catches a
    // rejection or a denial over HTTP catches it here.
    static void ThrowIfFailure(ScryStreamMarker marker)
    {
        if (marker.Kind != ScryStream.Error)
        {
            return;
        }

        throw ResponseFailure.Read(
            ScryJson.SerializeToUtf8(
                new ScryError(marker.Error ?? "The server gave no reason.")
                {
                    Code = marker.Code ?? ScryErrorCode.Unknown
                }));
    }
}
