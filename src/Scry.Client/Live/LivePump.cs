/// <summary>
/// Turns a transport that can be asked for a live query's answers — once, until its connection ends —
/// into answers that keep coming across connections: asking again when one ends, naming the last
/// answer held so it is not sent twice, and backing away from a server that keeps failing.
/// </summary>
/// <remarks>
/// <para>
/// Pulled, never pushed. An answer is read off the connection only when the consumer asks for the
/// next one, so a consumer that is slow leaves the answer unread, the server's write waits, and the
/// server answers once for however many changes arrived meanwhile. Nothing here holds a queue, which
/// is what keeps a slow consumer from being handed states that no longer hold.
/// </para>
/// <para>
/// A connection that stops without the server having said it was ending was cut, and is asked for
/// again exactly as one that failed to open is.
/// </para>
/// </remarks>
static class LivePump
{
    public static async IAsyncEnumerable<QueryResponse> Answers(
        ScryClient client,
        QueryRequest request,
        ScryCall? call,
        Action<ScrySubscriptionState>? changed,
        [EnumeratorCancellation] Cancel cancel)
    {
        string? lastId = null;
        QueryResponse? last = null;
        var failures = 0;
        var quietSince = Stopwatch.GetTimestamp();
        changed?.Invoke(ScrySubscriptionState.Connecting);

        while (true)
        {
            Exception? failure = null;
            var reconnect = true;
            var firstOfConnection = true;
            var frames = client.LiveAsync(request, call, lastId, cancel).GetAsyncEnumerator(cancel);
            try
            {
                while (true)
                {
                    LiveFrame frame;
                    try
                    {
                        if (!await frames.MoveNextAsync())
                        {
                            break;
                        }

                        frame = frames.Current;
                    }
                    catch (Exception exception) when (WorthAskingAgain(exception, cancel))
                    {
                        failure = exception;
                        break;
                    }

                    if (frame.Ended)
                    {
                        reconnect = frame.Reconnect;
                        break;
                    }

                    failures = 0;
                    quietSince = Stopwatch.GetTimestamp();
                    changed?.Invoke(ScrySubscriptionState.Live);

                    var repeated = firstOfConnection && Repeats(frame, last);
                    firstOfConnection = false;
                    if (frame.Response is not { } response ||
                        repeated)
                    {
                        continue;
                    }

                    lastId = frame.Id;
                    last = response;
                    yield return response;
                }
            }
            finally
            {
                await frames.DisposeAsync();
            }

            if (!reconnect)
            {
                yield break;
            }

            var delay = client.Reconnect.NextDelay(new(failures, Stopwatch.GetElapsedTime(quietSince), failure));
            if (delay is not { } wait)
            {
                throw failure ??
                      new ScryWireException("The live query's connection ended, and the retry policy declined to ask again.");
            }

            failures++;
            changed?.Invoke(ScrySubscriptionState.Reconnecting);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancel);
            }
        }
    }

    // A transport with no names for its answers re-sends the one already held each time it is asked
    // again. The first answer of a connection is therefore compared with the last one delivered, and
    // dropped where it says the same thing. Only the first: within a connection the server already
    // sends an answer only when it differs.
    static bool Repeats(LiveFrame frame, QueryResponse? last) =>
        frame is {Id: null, Response: { } response} &&
        last is not null &&
        response.Kind == last.Kind &&
        JsonElement.DeepEquals(response.Payload, last.Payload);

    /// <summary>
    /// Whether an ending is one a later attempt could get past. What the server refused on the
    /// request's own merits is not: it would refuse it again.
    /// </summary>
    static bool WorthAskingAgain(Exception exception, Cancel cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            // Busy, or failed while running: both are about the moment rather than the request.
            ScryRequestException {Code: ScryErrorCode.ExecutionFailed or ScryErrorCode.SubscriptionLimit} => true,

            // No code, so not the endpoint's own answer: something in the way, saying what it says
            // when the server behind it is restarting or overloaded.
            ScryRequestException {Code: ScryErrorCode.Unknown} unknown =>
                (int)unknown.StatusCode >= 500 ||
                unknown.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,

            // The connection could not be made, was cut, or timed out before headers arrived.
            HttpRequestException or IOException or TimeoutException => true,
            OperationCanceledException => true,
            _ => false
        };
    }
}
