using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;

namespace Scry;

public static partial class ScryServiceExtensions
{
    /// <summary>
    /// Answers a query, and then answers it again whenever the answer changes, as server-sent events
    /// on one long response. The request is the one the query endpoint takes, and each answer is the
    /// response that endpoint would have given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first answer is made before the response is committed, so everything a request can be
    /// refused for — malformed, rejected, denied, one live query too many — is still an ordinary status
    /// with an ordinary body. Past that point the status is sent, and what ends the stream says so in
    /// the stream: an <c>error</c> event for a failure, an <c>end</c> event for a reason of the
    /// server's own. A stream that stops with neither was cut.
    /// </para>
    /// <para>
    /// Everything that decides whether and when the query runs again is the processor's, so that a
    /// transport other than this one has it too. What is left here is the framing, and what only a
    /// connection has: a heartbeat, a lifetime, and the authentication ticket's expiry.
    /// </para>
    /// </remarks>
    static async Task HandleSubscribe(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        Advertise(context, processor, options);
        if (!await RequireJson(context))
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        QueryRequest request;
        try
        {
            request = ScryJson.DeserializeRequest(await ReadBody(context));
        }
        catch (ScryWireException exception)
        {
            QueryRecorder.Malformed(Stopwatch.GetElapsedTime(started));
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, ScryErrorCode.WireFormat);
            return;
        }

        var drifted = request.Stamp is { } stamp && stamp != processor.SchemaStamp;
        var db = (DbContext)services.GetRequiredService(options.ContextType);

        // Cancelled to end the query when this decides the stream is over, as well as when the client
        // goes: see LiveAnswers for why it has to be told rather than just dropped.
        using var ending = CancelSource.CreateLinkedTokenSource(context.RequestAborted);
        await using var answers = new LiveAnswers(
            processor.SubscribeBuffered(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                options.SubscriptionCaller(context),
                ending.Token),
            ending);

        if (!await First(context, answers, drifted))
        {
            return;
        }

        Commit(context);

        // A client asking again names the last answer it was given. The one it is about to be given
        // is often the same — it reconnected after a dropped connection, or because the server ended
        // the stream to bound its lifetime — and then it is told so rather than sent it.
        if (answers.Current.Id == context.Request.Headers[ScryLive.LastEventIdHeader].ToString())
        {
            await WriteEvent(context, ScryLive.Unchanged, id: null, data: default);
        }
        else
        {
            await WriteEvent(context, ScryLive.Result, answers.Current.Id, answers.Current.Json);
        }

        answers.Advance();
        await Pump(context, options, answers, drifted);
    }

    // The first answer, or the status that says why there is not one. True when there is.
    static async Task<bool> First(HttpContext context, LiveAnswers answers, bool drifted)
    {
        try
        {
            return await answers.Pending;
        }
        catch (ScryValidationException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, ErrorCodes.Classify(exception));
        }
        catch (ScryPermissionException exception)
        {
            await WriteError(context, StatusCodes.Status403Forbidden, exception.Message, ScryErrorCode.Forbidden);
        }
        catch (ScrySubscriptionLimitException exception)
        {
            // Nothing about the request was wrong, so the same one is worth sending again. The caller's
            // own limit is theirs to get back under and the server's is not, which is the difference
            // between the two statuses.
            context.Response.Headers.RetryAfter = retryAfterSeconds;
            await WriteError(
                context,
                exception.PerCaller ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable,
                exception.Message,
                ScryErrorCode.SubscriptionLimit);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Nobody is left to answer.
        }
        catch (Exception)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Query execution failed.", ErrorCodes.Failed(drifted));
        }

        return false;
    }

    static void Commit(HttpContext context)
    {
        var response = context.Response;
        response.ContentType = ScryLive.ContentType;

        // Rows shaped by who asked, on a response that never ends: nothing between here and the
        // caller has any business keeping it, or holding it back to send in larger pieces. The last
        // two say that to the places known to do so — a reverse proxy, and compression middleware.
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers.ContentEncoding = "identity";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    // Sends each answer as it arrives and a heartbeat when none has for a while, until the client
    // goes, the query fails, or the server has a reason of its own to end it.
    static async Task Pump(HttpContext context, ScryOptions options, LiveAnswers answers, bool drifted)
    {
        var stopping = context.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? Cancel.None;
        var (deadline, reason) = Deadline(context, options);
        var nextPing = DateTimeOffset.UtcNow + options.SubscriptionHeartbeat;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var wake = deadline is { } at && at < nextPing ? at : nextPing;
            if (await Arrives(answers.Pending, wake, context.RequestAborted, stopping))
            {
                if (!await Answer(context, answers, drifted))
                {
                    return;
                }

                answers.Advance();

                // An answer says the connection is alive as well as a heartbeat does.
                nextPing = DateTimeOffset.UtcNow + options.SubscriptionHeartbeat;
                continue;
            }

            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            if (stopping.IsCancellationRequested)
            {
                await End(context, "shutdown");
                return;
            }

            if (deadline is { } due &&
                DateTimeOffset.UtcNow >= due)
            {
                await End(context, reason);
                return;
            }

            await WriteEvent(context, ScryLive.Ping, id: null, data: default);

            // Kept to the clock rather than measured from this write. A run that found nothing to say
            // happens on its own task and never delays this one, so a heartbeat is never late for a
            // reason a caller could read a write into.
            nextPing += options.SubscriptionHeartbeat;
            if (nextPing <= DateTimeOffset.UtcNow)
            {
                nextPing = DateTimeOffset.UtcNow + options.SubscriptionHeartbeat;
            }
        }
    }

    // Whether the next answer arrived before it was time to do something else.
    static async Task<bool> Arrives(Task<bool> pending, DateTimeOffset wake, Cancel aborted, Cancel stopping)
    {
        var wait = wake - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            return pending.IsCompleted;
        }

        // Linked so that an answer arriving first releases the timer rather than leaving it to run out.
        using var waking = CancelSource.CreateLinkedTokenSource(aborted, stopping);
        var first = await Task.WhenAny(pending, Task.Delay(wait, waking.Token));
        await waking.CancelAsync();
        return first == pending;
    }

    // One answer out, or the event that says why there will be no more. True to keep going.
    static async Task<bool> Answer(HttpContext context, LiveAnswers answers, bool drifted)
    {
        try
        {
            if (!await answers.Pending)
            {
                return false;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }
        catch (ScryValidationException exception)
        {
            // The client's own doing and safe to repeat: the answer outgrew what a live query may
            // hold, or the model it was validated against has moved under it.
            await Fail(context, exception.Message, ErrorCodes.Classify(exception));
            return false;
        }
        catch (ScryPermissionException exception)
        {
            await Fail(context, exception.Message, ScryErrorCode.Forbidden);
            return false;
        }
        catch (Exception)
        {
            // The status is long since sent, so this is the only channel left, and it says no more
            // than the 500 would have.
            await Fail(context, "Query execution failed.", ErrorCodes.Failed(drifted));
            return false;
        }

        await WriteEvent(context, ScryLive.Result, answers.Current.Id, answers.Current.Json);
        return true;
    }

    static Task Fail(HttpContext context, string message, ScryErrorCode code) =>
        WriteEvent(
            context,
            ScryLive.Error,
            id: null,
            ScryJson.SerializeToUtf8(
                new ScryError(message)
                {
                    Code = code
                }));

    // Ended to be asked again. Reconnecting is a new request, authenticated and authorized as one —
    // which is the whole reason a live query is not allowed to last for ever.
    static Task End(HttpContext context, string reason) =>
        WriteEvent(
            context,
            ScryLive.End,
            id: null,
            ScryJson.SerializeToUtf8(
                new ScryLiveEnd(true)
                {
                    Reason = reason
                }));

    /// <summary>
    /// When this connection has to end whatever happens on it: the configured lifetime, or the moment
    /// the ticket that authenticated it stops being valid, whichever comes first.
    /// </summary>
    /// <remarks>
    /// Read off the feature the authentication middleware leaves rather than by authenticating again:
    /// a host with no default scheme has nothing to authenticate with, and would throw.
    /// </remarks>
    static (DateTimeOffset? At, string Reason) Deadline(HttpContext context, ScryOptions options)
    {
        var lifetime = options.SubscriptionLifetime is { } span
            ? DateTimeOffset.UtcNow + span
            : (DateTimeOffset?)null;
        var ticket = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        if (ticket is { } expires &&
            (lifetime is null || expires < lifetime))
        {
            return (expires, "expired");
        }

        return (lifetime, "lifetime");
    }

    /// <summary>
    /// Writes one server-sent event and flushes it. Written by hand rather than through the
    /// framework's formatter, which never flushes of its own accord and has no way to be handed bytes
    /// that are only valid until the next answer.
    /// </summary>
    /// <remarks>
    /// Every event carries a <c>data:</c> line, including the two with nothing to say: the platform's
    /// parser does not dispatch an event without one. The data is one line by construction — it is
    /// JSON from a writer that does not indent, and JSON escapes a line break inside a string.
    /// </remarks>
    static async Task WriteEvent(HttpContext context, string name, string? id, ReadOnlyMemory<byte> data)
    {
        Debug.Assert(data.Span.IndexOf((byte)'\n') < 0, "A server-sent event's data must be one line.");

        var cancel = context.RequestAborted;
        var body = context.Response.Body;
        var head = id is null ? $"event: {name}\ndata: " : $"event: {name}\nid: {id}\ndata: ";
        await body.WriteAsync(Encoding.UTF8.GetBytes(head), cancel);
        if (!data.IsEmpty)
        {
            await body.WriteAsync(data, cancel);
        }

        await body.WriteAsync(eventEnd, cancel);
        await body.FlushAsync(cancel);
    }

    static byte[] eventEnd = "\n\n"u8.ToArray();

    const string retryAfterSeconds = "5";

    /// <summary>
    /// A live query's answers, with the one being waited for kept in hand. An async iterator refuses
    /// to be disposed while it is being asked for its next value, so ending one means cancelling it,
    /// letting that request finish, and only then disposing — in that order, every time, which is the
    /// reason this exists rather than three lines at each place a stream can end.
    /// </summary>
    sealed class LiveAnswers(IAsyncEnumerable<SubscriptionFrame> source, CancelSource ending) :
        IAsyncDisposable
    {
        IAsyncEnumerator<SubscriptionFrame> answers = source.GetAsyncEnumerator(ending.Token);
        Task<bool>? pending;

        /// <summary>The answer being waited for. Asked for on first use, and again after each <see cref="Advance"/>.</summary>
        public Task<bool> Pending => pending ??= answers.MoveNextAsync().AsTask();

        public SubscriptionFrame Current => answers.Current;

        public void Advance() =>
            pending = null;

        public async ValueTask DisposeAsync()
        {
            await ending.CancelAsync();
            if (pending is not null)
            {
                try
                {
                    await pending;
                }
                catch (Exception)
                {
                    // Already answered for, or the cancellation just asked for.
                }
            }

            await answers.DisposeAsync();
        }
    }
}
