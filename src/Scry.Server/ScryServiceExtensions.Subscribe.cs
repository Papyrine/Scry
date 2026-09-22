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
        // goes: see HeldItems for why it has to be told rather than just dropped.
        using var ending = CancelSource.CreateLinkedTokenSource(context.RequestAborted);
        await using var answers = new HeldItems<SubscriptionFrame>(
            processor.SubscribeBuffered(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                options.Caller(context),
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
        await Hold(
            context,
            options,
            () => answers.Pending,
            async () =>
            {
                if (!await Answer(context, answers, drifted))
                {
                    return false;
                }

                answers.Advance();
                return true;
            });
    }

    // The first answer, or the status that says why there is not one. True when there is.
    static async Task<bool> First(HttpContext context, HeldItems<SubscriptionFrame> answers, bool drifted)
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

    // One answer out, or the event that says why there will be no more. True to keep going.
    static async Task<bool> Answer(HttpContext context, HeldItems<SubscriptionFrame> answers, bool drifted)
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
}
