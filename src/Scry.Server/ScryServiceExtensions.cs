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
        services.AddSingleton(options);
        services.AddSingleton(new ScryProcessor(schema, options));
        return services;
    }

    /// <summary>Maps the query-execution endpoint (HTTP POST) at <paramref name="pattern"/>.</summary>
    public static IEndpointConventionBuilder MapScry(this IEndpointRouteBuilder endpoints, string pattern)
    {
        // Validate the annotations against the live EF model once, at startup, where a model exists.
        // A misapplied [Queryable]/[QueryableComplex] fails loudly here rather than obscurely per-query.
        using (var scope = endpoints.ServiceProvider.CreateScope())
        {
            var options = scope.ServiceProvider.GetRequiredService<ScryOptions>();
            var processor = scope.ServiceProvider.GetRequiredService<ScryProcessor>();
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);
            processor.ValidateAgainstModel(db);
        }

        // One call, every transport of the same query surface: streaming reads it a row at a time and
        // batching carries several queries at once, but neither widens what can be asked. Mapping them
        // separately would only invite deployments where one is protected and the others are not.
        // Conventions applied to the returned builder reach all four.
        //
        // The attachment endpoint belongs here for that reason above all: it answers about a row of the
        // same model, so a deployment that authorizes queries and leaves it open would be handing out
        // by key exactly what the guard on the others exists to protect.
        return new Endpoints(
        [
            endpoints.MapPost(pattern, Handle),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/stream", HandleStream),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/batch", HandleBatch),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/attachment", HandleAttachment)
        ]);
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

        context.Response.Headers[WireFormat.SchemaStampHeader] = processor.SchemaStamp;

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
            (begin, diverting, rows) = processor.StreamBuffered(
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
        catch (Exception)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Query execution failed.", drifted);
            return;
        }

        // A plan with a [BinaryTransfer] slot commits to multipart before the first byte — the
        // decision is the plan's, not the data's, so an all-null result still wraps. Sections of
        // ndjson lines alternate with each row's binary parts, and every part precedes the line that
        // references it; a reader therefore holds at most one row's parts. Markers are ordinary lines.
        var multipart = diverting ? MultipartWriter.Create(context.Response.Body) : null;
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

        context.Response.Headers[WireFormat.SchemaStampHeader] = processor.SchemaStamp;

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
            using var buffer = new PooledBufferWriter();
            var collector = new BinaryPartCollector();
            processor.ExecuteBatchBuffered(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                buffer,
                collector);

            // One flat multipart for the whole batch: the collector threads through every entry, so
            // the parts are numbered globally and the batch envelope arrives last, referencing them.
            if (collector.Count > 0)
            {
                var multipart = MultipartWriter.Create(context.Response.Body);
                context.Response.ContentType = multipart.ContentType;
                foreach (var part in collector.Parts)
                {
                    await multipart.WriteBinary(part, context.RequestAborted);
                }

                await multipart.OpenPart("application/json", context.RequestAborted);
                await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
                await multipart.Terminate(context.RequestAborted);
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
        }
        catch (ScryValidationException exception)
        {
            // Envelope-level only: a per-entry rejection never reaches here.
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, exception.StaleClient);
        }
        catch (Exception)
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
    /// A declared length only sizes the buffer; it is never trusted as the amount to read. A body shorter
    /// than its Content-Length stays what it is today — a short buffer, so a parse failure and a 400 —
    /// rather than an exhausted read surfacing as a 500.
    /// </remarks>
    static async Task<byte[]> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength is > 0 and <= int.MaxValue)
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

        // No declared length (a chunked body). Nothing to size from, so it grows and is copied out once.
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        return buffer.ToArray();
    }

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

        // Advertised on every response, including rejections, so a client can notice a drifted model
        // while its queries are still succeeding rather than only once one breaks. Set before any
        // write, since headers are fixed once the response has started.
        context.Response.Headers[WireFormat.SchemaStampHeader] = processor.SchemaStamp;

        var started = Stopwatch.GetTimestamp();
        var body = await ReadBody(context);

        QueryRequest request;
        try
        {
            request = ScryJson.DeserializeRequest(body);
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
            using var buffer = new PooledBufferWriter();
            var collector = new BinaryPartCollector();
            var buffered = processor.TryExecuteBuffered(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                buffer,
                out var response,
                collector);

            // A result that diverted [BinaryTransfer] values travels as multipart: the raw parts
            // first, then the JSON envelope that references them. The envelope is buffered either way,
            // so parts-first is free. Anything else is today's plain JSON, byte for byte.
            if (collector.Count > 0)
            {
                var multipart = MultipartWriter.Create(context.Response.Body);
                context.Response.ContentType = multipart.ContentType;
                foreach (var part in collector.Parts)
                {
                    await multipart.WriteBinary(part, context.RequestAborted);
                }

                await multipart.OpenPart("application/json", context.RequestAborted);
                if (buffered)
                {
                    await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
                }
                else
                {
                    await context.Response.Body.WriteAsync(ScryJson.SerializeToUtf8(response!), context.RequestAborted);
                }

                await multipart.Terminate(context.RequestAborted);
                return;
            }

            context.Response.ContentType = "application/json";
            if (buffered)
            {
                await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
            }
            else
            {
                await context.Response.Body.WriteAsync(ScryJson.SerializeToUtf8(response!), context.RequestAborted);
            }
        }
        catch (ScryValidationException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, exception.StaleClient);
        }
        catch (Exception)
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
        context.Response.Headers[WireFormat.SchemaStampHeader] = processor.SchemaStamp;

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
            var result = processor.FetchAttachment(request, db, services, context.Request.Headers, context.Response.Headers);

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

            context.Response.ContentType = ScryBinary.PartContentType;
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

    static Task WriteError(HttpContext context, int status, string message, bool staleClient)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json";
        return response.WriteAsJsonAsync(
            new ScryError(message)
            {
                StaleClient = staleClient
            },
            ScryJson.Options,
            context.RequestAborted);
    }
}
