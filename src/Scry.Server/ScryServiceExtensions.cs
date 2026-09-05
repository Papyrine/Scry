namespace Scry;

/// <summary>Registration and endpoint wiring for the Scry server.</summary>
public static class ScryServiceExtensions
{
    /// <summary>Registers the query executor and builds the allow-list schema from the model.</summary>
    public static IServiceCollection AddScry<TContext>(this IServiceCollection services, Action<ScryOptions> configure)
        where TContext : DbContext
    {
        var options = new ScryOptions(typeof(TContext));
        configure(options);

        var schema = Schema.Build(options);
        var processor = new ScryProcessor(schema, options);
        services.AddSingleton(options);
        services.AddSingleton(processor);
        services.AddSingleton(processor.PolicyCache);
        return services;
    }

    /// <summary>Maps the query-execution endpoint at <paramref name="pattern"/>.</summary>
    public static IEndpointConventionBuilder MapScry(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ScryOptions options;

        // Validate the annotations against the live EF model once, at startup, where a model exists.
        // A misapplied [Queryable]/[QueryableComplex] fails loudly here rather than obscurely per-query.
        using (var scope = endpoints.ServiceProvider.CreateScope())
        {
            options = scope.ServiceProvider.GetRequiredService<ScryOptions>();
            var processor = scope.ServiceProvider.GetRequiredService<ScryProcessor>();
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);
            processor.ValidateAgainstModel(db);
            if (options.ProbePoliciedNavigations)
            {
                processor.ProbePoliciedNavigations(db, scope.ServiceProvider);
            }

            RefuseUnscopedCaching(options, processor);
        }

        // One call, every transport of the same query surface: streaming reads it a row at a time and
        // batching carries several queries at once, but neither widens what can be asked. Mapping them
        // separately would only invite deployments where one is protected and the others are not.
        // Conventions applied to the returned builder reach all of them.
        //
        // The attachment endpoint belongs here for that reason above all: it answers about a row of the
        // same model, so a deployment that authorizes queries and leaves it open would be handing out
        // by key exactly what the guard on the others exists to protect.
        List<IEndpointConventionBuilder> builders =
        [
            endpoints.MapPost(pattern, Handle),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/stream", HandleStream),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/batch", HandleBatch),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/attachment", HandleAttachment)
        ];

        // The same query, asked as a URL. One handler serves both: a GET carries the request in its
        // query string instead of its body, and nothing downstream of that difference changes — same
        // validation, same allow-list, same policies. It exists because a POST is uncacheable by
        // everything between the client and here, where a GET is answered by the caller's own cache and
        // revalidated with an ETag. See QueryUrl for why the request travels in the URL rather than in
        // content on the GET.
        //
        // A limit of zero says this deployment will not have queries in URLs at all, and the route is
        // simply not mapped: routing then answers a GET with a 405 naming POST, which is both the
        // accurate status and a capability that is absent rather than guarded — there is no handler for
        // a stale client to reach, and no URL for this server to read or log.
        if (options.QueryUrlLimit > 0)
        {
            builders.Insert(1, endpoints.MapGet(pattern, Handle));
        }

        return new Endpoints(builders);
    }

    /// <summary>
    /// Refuses at startup to answer conditionally from a source whose rows depend on who asked, unless
    /// the host has said what a cached response belongs to.
    /// </summary>
    /// <remarks>
    /// A row policy reads the request, so the same query answers differently for two callers while its
    /// URL — and therefore its ETag — says nothing about which one. A browser profile outlives a
    /// sign-out, so the next identity revalidates, matches, and is handed the previous one's rows.
    /// Loud here rather than silent there: caching is opt-in, so a host that asked for it is told
    /// exactly what else to set.
    /// </remarks>
    static void RefuseUnscopedCaching(ScryOptions options, ScryProcessor processor)
    {
        if (options.QueryFreshness is null ||
            options.CacheScope is not null ||
            processor.PolicedSource is not { } source)
        {
            return;
        }

        throw new($"'{source}' carries a policy, so its rows depend on who asked — and a cached response identified by a URL does not say who that was. Set ScryOptions.CacheScope to what such a response belongs to (a tenant, a principal), or leave ScryOptions.QueryFreshness unset to answer nothing conditionally.");
    }

    sealed class Endpoints(IReadOnlyList<IEndpointConventionBuilder> builders) :
        IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention)
        {
            foreach (var builder in builders)
            {
                builder.Add(convention);
            }
        }

        public void Finally(Action<EndpointBuilder> convention)
        {
            foreach (var builder in builders)
            {
                builder.Finally(convention);
            }
        }
    }

    /// <summary>
    /// Streams a list result as newline-delimited JSON. Validation has already run to completion by the
    /// time the first row is pulled, so a rejection is still a 400 with a body; past that point the
    /// status is committed and a failure is reported through the stream's closing marker instead.
    /// </summary>
    static async Task HandleStream(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        Advertise(context, processor, options);

        var started = Stopwatch.GetTimestamp();
        var body = await ReadBody(context);

        QueryRequest request;
        try
        {
            request = ScryJson.DeserializeRequest(body);
        }
        catch (ScryWireException exception)
        {
            // Never reaches the processor, so it is metered here — an unparseable payload is a signal
            // the outcome tag exists for.
            QueryRecorder.Malformed(Stopwatch.GetElapsedTime(started));
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, staleClient: false);
            return;
        }

        var drifted = request.Stamp is { } stamp && stamp != processor.SchemaStamp;

        ScryStreamMarker begin;
        bool diverting;
        IAsyncEnumerable<ReadOnlyMemory<byte>> rows;
        var collector = new BinaryPartCollector();
        try
        {
            var db = (DbContext)services.GetRequiredService(options.ContextType);

            // The live response dictionary, so a policy's writes are already on the response rather
            // than needing a copy step that could run after the stream has started and headers are
            // fixed. Validation and policies both complete before Stream returns, so anything written
            // is in place before the begin marker below. Rows arrive as finished JSON bytes, written
            // by the projection's shape writer rather than through per-row dictionaries.
            (begin, diverting, rows) = await processor.StreamBufferedAsync(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                context.RequestAborted,
                collector);
        }
        catch (ScryValidationException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, exception.StaleClient);
            return;
        }
        catch (ScryPermissionException exception)
        {
            // The stream is built before its first byte is written, so a denial found while building
            // it still answers as a status — the response has not started.
            await WriteError(context, StatusCodes.Status403Forbidden, exception.Message, staleClient: false);
            return;
        }
        catch (Exception)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Query execution failed.", drifted);
            return;
        }

        // A plan with a [BinaryTransfer] slot commits to multipart before the first byte — the
        // decision is the plan's, not the data's, so an all-null result still wraps. Sections of
        // ndjson lines alternate with each row's binary parts, and every part precedes the line that
        // references it; a reader therefore holds at most one row's parts. Markers are ordinary lines.
        var multipart = diverting ? ScryMultipart.Create(context.Response.Body) : null;
        context.Response.ContentType = multipart?.ContentType ?? ScryStream.ContentType;
        if (multipart is not null)
        {
            await multipart.OpenPart(ScryStream.ContentType, context.RequestAborted);
        }

        await WriteLine(context, begin);

        try
        {
            await foreach (var row in rows)
            {
                // The row's bytes are written (and its binary values collected) before it is yielded,
                // so the parts drained here are exactly this row's. Draining is what resets the
                // placeholder indices per line.
                if (multipart is not null &&
                    collector.Count > 0)
                {
                    foreach (var part in collector.Drain())
                    {
                        await multipart.WriteBinary(part, context.RequestAborted);
                    }

                    await multipart.OpenPart(ScryStream.ContentType, context.RequestAborted);
                }

                await WriteLine(context, row);
            }
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // The status is long since sent, so this is the only channel left to say the rows are not
            // the whole answer. A validation message is the client's own doing and is safe to repeat;
            // anything else would leak internals, exactly as it would on a non-streamed response.
            await WriteLine(
                context,
                new ScryStreamMarker
                {
                    Kind = ScryStream.Error,
                    Error = exception is ScryValidationException ? exception.Message : "Query execution failed."
                });
            if (multipart is not null)
            {
                await multipart.Terminate(context.RequestAborted);
            }

            return;
        }

        await WriteLine(context, new ScryStreamMarker {Kind = ScryStream.End});
        if (multipart is not null)
        {
            await multipart.Terminate(context.RequestAborted);
        }
    }

    /// <summary>
    /// Executes a batch of queries as one request. Only an envelope failure — an unreadable body, an
    /// unsupported wire version, or more entries than <c>MaxBatchSize</c> — is a non-success status;
    /// a rejected or failed entry is reported in its own result alongside the entries that succeeded.
    /// </summary>
    static async Task HandleBatch(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        Advertise(context, processor, options);

        var started = Stopwatch.GetTimestamp();
        var body = await ReadBody(context);

        QueryBatchRequest request;
        try
        {
            request = ScryJson.DeserializeBatchRequest(body);
        }
        catch (ScryWireException exception)
        {
            QueryRecorder.Malformed(Stopwatch.GetElapsedTime(started));
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, staleClient: false);
            return;
        }

        try
        {
            var db = (DbContext)services.GetRequiredService(options.ContextType);

            // Each entry that is a list or page is written straight from its projected rows into the
            // batch envelope — no dictionaries, no JsonElement round trip, and no second pass over
            // every entry to serialize the envelope around them. An entry the writer cannot reproduce
            // is serialized into the same envelope; the bytes are what ExecuteBatch would have produced.
            using var spill = new ResponseSpill(context, options.ResponseSpillThreshold);
            var collector = new BinaryPartCollector();
            await processor.ExecuteBatchBufferedAsync(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                spill.Output,
                collector,
                spill,
                context.RequestAborted);

            // Something is already on the wire, which nothing on a model carrying a binary member is
            // ever allowed to put there — so the collector is empty by construction.
            if (spill.Committed)
            {
                await spill.CompleteAsync(context.RequestAborted);
                return;
            }

            // One flat multipart for the whole batch: the collector threads through every entry, so
            // the parts are numbered globally and the batch envelope arrives last, referencing them.
            if (collector.Count > 0)
            {
                var multipart = ScryMultipart.Create(context.Response.Body);
                context.Response.ContentType = multipart.ContentType;
                foreach (var part in collector.Parts)
                {
                    await multipart.WriteBinary(part, context.RequestAborted);
                }

                await multipart.OpenPart("application/json", context.RequestAborted);
                await context.Response.Body.WriteAsync(spill.Pending, context.RequestAborted);
                await multipart.Terminate(context.RequestAborted);
                return;
            }

            await spill.CompleteAsync(context.RequestAborted);
        }
        catch (ScryValidationException exception) when (!context.Response.HasStarted)
        {
            // Envelope-level only: a per-entry rejection never reaches here.
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, exception.StaleClient);
        }
        catch (ScryPermissionException exception) when (!context.Response.HasStarted)
        {
            // Envelope-level only in the same way: an entry's denial is answered in that entry's result.
            await WriteError(context, StatusCodes.Status403Forbidden, exception.Message, staleClient: false);
        }
        catch (Exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Query execution failed.", staleClient: false);
        }
    }

    /// <summary>
    /// Reads the request body as the UTF-8 it arrived as. The wire readers take those bytes directly, so
    /// decoding to a string first would transcode to UTF-16 only for the JSON reader to transcode back —
    /// and the bytes are what a caller fingerprinting the request would hash.
    /// </summary>
    /// <remarks>
    /// A declared length only sizes the buffer, and only up to <see cref="PresizeCeiling"/>; it is never
    /// trusted as the amount to read. A body shorter than its Content-Length stays what it is today — a
    /// short buffer, so a parse failure and a 400 — rather than an exhausted read surfacing as a 500.
    /// </remarks>
    internal static async Task<byte[]> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength is > 0 and <= PresizeCeiling)
        {
            var declared = (int) context.Request.ContentLength;
            var exact = new byte[declared];
            var read = await context.Request.Body.ReadAtLeastAsync(
                exact,
                declared,
                throwOnEndOfStream: false,
                context.RequestAborted);

            // The server never receives more than Content-Length — the host stops the body there — so a
            // full read is the whole body in one right-sized array. A short read means a truncated body,
            // which stays exactly what it was before: unparseable, and answered as a malformed request.
            return read == declared ? exact : exact[..read];
        }

        // No usable length: none was declared (a chunked body), or the one declared is more than is
        // taken on trust. The buffer grows with what actually arrives and is copied out once.
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        return buffer.ToArray();
    }

    /// <summary>
    /// The most a declared Content-Length is allowed to pre-size the body buffer. A length is a claim
    /// the client makes before sending a byte, and the host checks it against its own limit only on
    /// the first read — after this method has already sized to it. Sizing to the claim would let a
    /// request that declares gigabytes and then sends nothing hold that much memory for as long as
    /// the host keeps the connection open. Above the ceiling the buffer grows with the bytes that
    /// arrive, so a body costs memory in proportion to what was actually sent.
    /// </summary>
    internal const int PresizeCeiling = 64 * 1024;

    static Task WriteLine(HttpContext context, ScryStreamMarker marker) =>
        WriteLine(context, ScryJson.Serialize(marker));

    static async Task WriteLine(HttpContext context, ReadOnlyMemory<byte> json)
    {
        await context.Response.Body.WriteAsync(json, context.RequestAborted);
        await context.Response.WriteAsync("\n", context.RequestAborted);

        // Flushed per line: a stream a client reads incrementally is the point, and a buffered response
        // would deliver it as one block anyway.
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    static async Task WriteLine(HttpContext context, string json)
    {
        await context.Response.WriteAsync(json, context.RequestAborted);
        await context.Response.WriteAsync("\n", context.RequestAborted);

        // Flushed per line: a stream a client reads incrementally is the point, and a buffered response
        // would deliver it as one block anyway.
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    static async Task Handle(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        Advertise(context, processor, options);

        var started = Stopwatch.GetTimestamp();
        var url = HttpMethods.IsGet(context.Request.Method);

        if (url)
        {
            // A URL identifies a response, so a cache may keep this one — but only the cache belonging
            // to the caller. Rows are shaped by policies that read the request, so the same URL answers
            // differently for two principals and a shared cache would hand one of them the other's
            // rows. `no-cache` keeps a stored copy revalidating rather than expiring on a guess: with
            // no validator on the response, a browser is free to invent a freshness lifetime and serve
            // stale rows without asking. An app that knows better can widen this above the endpoint.
            context.Response.Headers.CacheControl = "private, no-cache";

            // Answered from what the caller already holds, where the host has said how to tell that
            // nothing has changed. Before the request is decoded, since proving a repeat is the whole
            // point of not doing the work — and after Cache-Control above, so a 304 carries both
            // directives: a client merges a 304's headers into the response it kept, and `no-cache`
            // alone would strip `private` from its stored copy.
            if (await QueryEtag.NotModified(context, processor, options))
            {
                return;
            }
        }

        QueryRequest request;
        try
        {
            request = url
                ? QueryUrl.Decode(context.Request.Query[QueryUrl.Parameter])
                : ScryJson.DeserializeRequest(await ReadBody(context));
        }
        catch (ScryWireException exception)
        {
            // A malformed request carries no usable stamp, so it is never attributed to staleness.
            // It also never reaches the processor, so it is metered here.
            QueryRecorder.Malformed(Stopwatch.GetElapsedTime(started));
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, staleClient: false);
            return;
        }

        // A stamp that disagrees marks the client as generated against a different model surface.
        // Validation failures carry their own attribution (ScryValidationException.StaleClient); for
        // an execution failure this is the only attribution available — a drifted client faulting the
        // server is far more likely stale than the server broken, and marking it lets the client
        // prompt a reload instead of presenting an unexplained server error.
        var drifted = request.Stamp is { } stamp && stamp != processor.SchemaStamp;

        try
        {
            var db = (DbContext)services.GetRequiredService(options.ContextType);

            // The result is written straight from the projected values — no dictionaries, no
            // JsonElement round trip — whatever its kind. Only a drifted client's alias-carrying
            // envelope comes back as a QueryResponse to be serialized the general way; the two
            // produce identical bytes.
            using var spill = new ResponseSpill(context, options.ResponseSpillThreshold);
            var collector = new BinaryPartCollector();
            var fallback = await processor.TryExecuteBufferedAsync(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                spill.Output,
                spill,
                collector,
                context.RequestAborted,
                url);

            // The writer declined this one, so the buffer it was handed is untouched — the envelope is
            // serialized into it rather than into a right-sized array that would be written once and
            // dropped. Past here the response is one span whichever produced it.
            if (fallback is not null)
            {
                ResponseWriter.Write(spill.Output, fallback);
            }

            // Something is already on the wire, which only a plan carrying no binary slot is ever
            // allowed to put there — so the collector is empty by construction and there is nothing
            // left to decide, only the tail to send.
            if (spill.Committed)
            {
                await spill.CompleteAsync(context.RequestAborted);
                return;
            }

            // A result that diverted [BinaryTransfer] values travels as multipart: the raw parts
            // first, then the JSON envelope that references them. Such a result is never allowed to
            // spill, so the envelope is whole here and parts-first is free. Anything else is today's
            // plain JSON, byte for byte.
            if (collector.Count > 0)
            {
                var multipart = ScryMultipart.Create(context.Response.Body);
                context.Response.ContentType = multipart.ContentType;
                foreach (var part in collector.Parts)
                {
                    await multipart.WriteBinary(part, context.RequestAborted);
                }

                await multipart.OpenPart("application/json", context.RequestAborted);
                await context.Response.Body.WriteAsync(spill.Pending, context.RequestAborted);
                await multipart.Terminate(context.RequestAborted);
                return;
            }

            await spill.CompleteAsync(context.RequestAborted);
        }
        catch (ScryValidationException exception) when (!context.Response.HasStarted)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, exception.StaleClient, exception.RequiresBody);
        }
        catch (ScryPermissionException exception) when (!context.Response.HasStarted)
        {
            await WriteError(context, StatusCodes.Status403Forbidden, exception.Message, staleClient: false);
        }
        catch (Exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            // Never leak internals (stack traces, SQL) to the client.
            await WriteError(context, StatusCodes.Status500InternalServerError, "Query execution failed.", drifted);
        }
    }

    static async Task HandleAttachment(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        // Advertised here for the same reason as on a query, and on a 404 too: a client whose fetch
        // stopped working wants to know whether its model drifted.
        Advertise(context, processor, options);

        var started = Stopwatch.GetTimestamp();
        var body = await ReadBody(context);

        AttachmentRequest request;
        try
        {
            request = ScryJson.DeserializeAttachmentRequest(body);
        }
        catch (ScryWireException exception)
        {
            QueryRecorder.Malformed(Stopwatch.GetElapsedTime(started));
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, staleClient: false);
            return;
        }

        var drifted = request.Stamp is { } stamp && stamp != processor.SchemaStamp;

        try
        {
            var db = (DbContext) services.GetRequiredService(options.ContextType);
            var result = await processor.FetchAttachmentAsync(request, db, services, context.Request.Headers, context.Response.Headers, context.RequestAborted);

            // Refused, absent, and policy-filtered arrive here as one answer and leave as one status.
            // A body would only give a caller holding a guessed key something to tell them apart by.
            if (!result.Found)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // The row was readable and its column holds nothing. Distinct from the 404 above: this
            // says the value is absent, not that the caller may not have it.
            if (result.Value is not { } value)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // What the model said the bytes are, or what the policy said this row's are. Sent nosniff
            // because a declared type is a statement about a column, and the bytes under it are
            // whatever was stored: a browser re-deciding from the content is the one way a wrong
            // label becomes a wrong behaviour.
            context.Response.ContentType = result.ContentType ?? AttachmentMedia.Default;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.ContentLength = value.Length;
            await context.Response.Body.WriteAsync(value, context.RequestAborted);
        }
        catch (ScryValidationException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, exception.StaleClient);
        }
        catch (Exception)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Attachment fetch failed.", drifted);
        }
    }

    /// <summary>
    /// What every response says about this server regardless of what it was asked, written before any
    /// body since headers are fixed once a response has started.
    /// </summary>
    /// <remarks>
    /// The stamp lets a client notice a drifted model while its queries are still succeeding, rather
    /// than only once one breaks — which is why it is on rejections too. The URL limit rides the same
    /// path for the same reason: a client that has heard from this server once never has to be told its
    /// budget out of band, and one that has not yet heard uses QueryUrl.MaxLength until it does.
    /// </remarks>
    static void Advertise(HttpContext context, ScryProcessor processor, ScryOptions options)
    {
        var headers = context.Response.Headers;
        headers[WireFormat.SchemaStampHeader] = processor.SchemaStamp;
        headers[WireFormat.UrlLimitHeader] = options.QueryUrlLimit.ToString(CultureInfo.InvariantCulture);
    }

    static Task WriteError(
        HttpContext context,
        int status,
        string message,
        bool staleClient,
        bool requiresBody = false)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json";

        // A refusal is never the thing to keep: this one exists to be retried in a body, and the
        // header set for the query it refused would otherwise let a cache answer for it.
        response.Headers.CacheControl = "no-store";
        return response.WriteAsJsonAsync(
            new ScryError(message)
            {
                StaleClient = staleClient,
                RequiresBody = requiresBody
            },
            ScryJson.Options,
            context.RequestAborted);
    }
}
