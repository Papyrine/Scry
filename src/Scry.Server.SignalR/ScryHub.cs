namespace Scry;

/// <summary>
/// The Scry query surface over a SignalR hub: what <c>MapScry</c> serves over HTTP, served over one
/// connection instead. What it is for is live queries — a page holding a dozen of them holds one
/// socket rather than a dozen requests — and for a host that already routes everything through
/// SignalR. Map it with <c>MapScryHub</c>, and derive from it to carry an <c>[Authorize]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Requests arrive and answers leave as strings of the same JSON the HTTP endpoints speak, read and
/// written by <see cref="ScryJson"/>. A hub would otherwise bind its arguments with its own
/// serializer, whose options know nothing of what makes the wire format fail closed — an unknown
/// member refused, a duplicated property refused, a null array element refused — and would write a
/// result's enums as numbers.
/// </para>
/// <para>
/// Everything a query is subject to applies unchanged, because it is the same processor: validation,
/// the allow-list, row policies, auditing, and for a live query the limits on how many may be open.
/// SignalR itself puts no bound on how many streams one client starts.
/// </para>
/// <para>
/// Row policies are given the headers of the request that opened the connection, since a hub call has
/// none of its own, and what they write to the response goes nowhere. Attachments are fetched over
/// HTTP as they always are: a hub has nothing to offer a download.
/// </para>
/// </remarks>
public class ScryHub(ScryProcessor processor, ScryOptions options, IServiceProvider services) :
    Hub
{
    /// <summary>Answers one query.</summary>
    public async Task<string> Query(string request)
    {
        if (Parse(request, out var parsed) is { } malformed)
        {
            return malformed;
        }

        try
        {
            using var output = new PooledBufferWriter();
            var fallback = await processor.TryExecuteBufferedAsync(
                parsed,
                Data,
                services,
                RequestHeaders,
                new HeaderDictionary(),
                output,
                cancel: Context.ConnectionAborted);
            if (fallback is not null)
            {
                return ScryJson.Serialize(fallback);
            }

            return Encoding.UTF8.GetString(output.WrittenMemory.Span);
        }
        catch (Exception exception) when (!Context.ConnectionAborted.IsCancellationRequested)
        {
            return Failure(exception, Drifted(parsed));
        }
    }

    /// <summary>Answers several queries at once.</summary>
    public async Task<string> Batch(string request)
    {
        QueryBatchRequest parsed;
        try
        {
            parsed = ScryJson.DeserializeBatchRequest(request);
        }
        catch (ScryWireException exception)
        {
            return Failure(exception.Message, ScryErrorCode.WireFormat);
        }

        try
        {
            using var output = new PooledBufferWriter();
            await processor.ExecuteBatchBufferedAsync(
                parsed,
                Data,
                services,
                RequestHeaders,
                new HeaderDictionary(),
                output,
                binary: null,
                cancel: Context.ConnectionAborted);
            return Encoding.UTF8.GetString(output.WrittenMemory.Span);
        }
        catch (Exception exception) when (!Context.ConnectionAborted.IsCancellationRequested)
        {
            return Failure(exception, drifted: false);
        }
    }

    /// <summary>
    /// Answers one query a row at a time: the lines the stream endpoint writes, its opening and
    /// closing markers included, so the client reads them with the reader it already has.
    /// </summary>
    public async IAsyncEnumerable<string> Stream(string request, [EnumeratorCancellation] Cancel cancel)
    {
        if (Parse(request, out var parsed) is { } malformed)
        {
            yield return malformed;
            yield break;
        }

        var drifted = Drifted(parsed);
        string? failure = null;
        ScryStreamMarker? begin = null;
        IAsyncEnumerable<ReadOnlyMemory<byte>>? rows = null;
        try
        {
            (begin, _, rows) = await processor.StreamBufferedAsync(
                parsed,
                Data,
                services,
                RequestHeaders,
                new HeaderDictionary(),
                cancel);
        }
        catch (Exception exception) when (!cancel.IsCancellationRequested)
        {
            failure = Failure(exception, drifted);
        }

        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        yield return ScryJson.Serialize(begin!);
        await using var enumerator = rows!.GetAsyncEnumerator(cancel);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }
            }
            catch (Exception exception) when (!cancel.IsCancellationRequested)
            {
                failure = Failure(exception, drifted);
            }

            if (failure is not null)
            {
                yield return failure;
                yield break;
            }

            yield return Encoding.UTF8.GetString(enumerator.Current.Span);
        }

        yield return ScryJson.Serialize(
            new ScryStreamMarker
            {
                Kind = ScryStream.End
            });
    }

    /// <summary>
    /// Answers one query, and again whenever its answer changes, until the client stops the stream or
    /// the connection goes. Each item is a complete response; a failure is a closing marker.
    /// </summary>
    public async IAsyncEnumerable<string> Subscribe(string request, [EnumeratorCancellation] Cancel cancel)
    {
        if (Parse(request, out var parsed) is { } malformed)
        {
            yield return malformed;
            yield break;
        }

        // Over HTTP a server with no limit set maps no route. A hub's methods are its type's, so the
        // nearest thing to an absent capability is one that says it is absent — as a rejection rather
        // than as a limit reached, since a limit is worth asking again about and this is not.
        if (options.MaxSubscriptions <= 0)
        {
            yield return Failure(
                $"This server is not serving live queries: ScryOptions.{nameof(options.MaxSubscriptions)} is zero.",
                ScryErrorCode.Validation);
            yield break;
        }

        var drifted = Drifted(parsed);
        string? failure = null;
        await using var answers = processor
            .SubscribeBuffered(
                parsed,
                Data,
                services,
                RequestHeaders,
                new HeaderDictionary(),
                Context.UserIdentifier,
                cancel)
            .GetAsyncEnumerator(cancel);
        while (true)
        {
            try
            {
                if (!await answers.MoveNextAsync())
                {
                    break;
                }
            }
            catch (Exception exception) when (!cancel.IsCancellationRequested)
            {
                failure = Failure(exception, drifted);
            }

            if (failure is not null)
            {
                yield return failure;
                yield break;
            }

            yield return Encoding.UTF8.GetString(answers.Current.Json.Span);
        }
    }

    DbContext Data =>
        (DbContext)services.GetRequiredService(options.ContextType);

    // The headers of the request that opened the connection: the only ones a hub call has.
    IHeaderDictionary RequestHeaders =>
        Context.GetHttpContext()?.Request.Headers ?? new HeaderDictionary();

    bool Drifted(QueryRequest request) =>
        request.Stamp is { } stamp &&
        stamp != processor.SchemaStamp;

    // Null when the request was read; otherwise what to answer instead.
    static string? Parse(string request, out QueryRequest parsed)
    {
        try
        {
            parsed = ScryJson.DeserializeRequest(request);
            return null;
        }
        catch (ScryWireException exception)
        {
            parsed = null!;
            return Failure(exception.Message, ScryErrorCode.WireFormat);
        }
    }

    // The same answers the endpoints give, coded the same way: what the client did is repeated to
    // it, and anything else is the fixed text and nothing more.
    static string Failure(Exception exception, bool drifted) =>
        exception switch
        {
            ScryValidationException validation => Failure(validation.Message, ErrorCodes.Classify(validation)),
            ScryPermissionException permission => Failure(permission.Message, ScryErrorCode.Forbidden),
            ScrySubscriptionLimitException limit => Failure(limit.Message, ScryErrorCode.SubscriptionLimit),
            _ => Failure("Query execution failed.", ErrorCodes.Failed(drifted))
        };

    static string Failure(string message, ScryErrorCode code) =>
        ScryJson.Serialize(
            new ScryStreamMarker
            {
                Kind = ScryStream.Error,
                Error = message,
                Code = code
            });
}
