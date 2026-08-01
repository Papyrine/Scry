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
        // Conventions applied to the returned builder reach all three.
        return new Endpoints(
        [
            endpoints.MapPost(pattern, Handle),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/stream", HandleStream),
            endpoints.MapPost($"{pattern.TrimEnd('/')}/batch", HandleBatch)
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
        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

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
        IAsyncEnumerable<Dictionary<string, object?>> rows;
        try
        {
            var db = (DbContext)services.GetRequiredService(options.ContextType);

            // The live response dictionary, so a policy's writes are already on the response rather
            // than needing a copy step that could run after the stream has started and headers are
            // fixed. Validation and policies both complete before Stream returns, so anything written
            // is in place before the begin marker below.
            (begin, rows) = processor.Stream(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers,
                context.RequestAborted);
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

        context.Response.ContentType = ScryStream.ContentType;
        await WriteLine(context, begin);

        try
        {
            await foreach (var row in rows)
            {
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
            return;
        }

        await WriteLine(context, new ScryStreamMarker {Kind = ScryStream.End});
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
        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

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
            var response = processor.ExecuteBatch(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ScryJson.Serialize(response), context.RequestAborted);
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

    static Task WriteLine(HttpContext context, ScryStreamMarker marker) =>
        WriteLine(context, ScryJson.Serialize(marker));

    // A row's shape comes from the query rather than from a wire type — it is the projected members
    // the client asked for — so there is nothing to generate metadata for ahead of time.
    static Task WriteLine(HttpContext context, Dictionary<string, object?> row) =>
        WriteLine(context, JsonSerializer.Serialize(row, ScryJson.Options));

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
        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

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
        // server (e.g. a constant parsed against a member whose type has since changed) is far more
        // likely stale than the server broken, and marking it lets the client prompt a reload instead
        // of presenting an unexplained server error.
        var drifted = request.Stamp is { } stamp && stamp != processor.SchemaStamp;

        try
        {
            var db = (DbContext)services.GetRequiredService(options.ContextType);
            var response = processor.Execute(
                request,
                db,
                services,
                context.Request.Headers,
                context.Response.Headers);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ScryJson.Serialize(response), context.RequestAborted);
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
