namespace Scry;

/// <summary>
/// Executes a query request against a <see cref="DbContext"/>, applying validation, allow-list,
/// policies, and shaping. This is the programmatic entry point used by the HTTP endpoint and is also
/// usable directly (other transports, tests).
/// </summary>
public sealed class ScryProcessor
{
    QueryExecutor executor;
    Schema schema;
    ScryOptions options;

    internal ScryProcessor(Schema schema, ScryOptions options)
    {
        this.schema = schema;
        this.options = options;
        executor = new(schema, options);
    }

    /// <summary>Describes the allow-listed query surface for tooling (the query explorer).</summary>
    public ScryIntrospection Describe() => schema.Describe(options);

    /// <summary>
    /// A hash of this server's allow-listed surface. Advertised on every response so a client can
    /// compare it against the stamp it was generated with and detect a drifted model.
    /// </summary>
    public string SchemaStamp => schema.Stamp;

    /// <summary>
    /// Confirms the model's annotations match its live EF mapping (e.g. a <c>[Queryable]</c> type is
    /// really an entity, a <c>[QueryableComplex]</c> type is really a complex type), throwing a
    /// directed error otherwise. Called once at startup by <c>MapScry</c>; safe to call from other
    /// hosts that have a <see cref="DbContext"/>.
    /// </summary>
    public void ValidateAgainstModel(DbContext data) =>
        schema.ValidateAgainstModel(data.Model, options.ContextType);

    /// <summary>Builds a processor from configuration (e.g. for tests or non-DI hosting).</summary>
    public static ScryProcessor Create<TContext>(Action<ScryOptions> configure)
        where TContext : DbContext
    {
        var options = new ScryOptions(typeof(TContext));
        configure(options);
        return new(Schema.Build(options), options);
    }

    /// <summary>Validates and executes a request, returning the shaped result.</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data, IServiceProvider services) =>
        Execute(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>
    /// Validates and executes a request, exposing <paramref name="requestHeaders"/> to row policies and
    /// letting them write to <paramref name="responseHeaders"/>.
    /// </summary>
    /// <remarks>
    /// The HTTP endpoint passes the live <see cref="HttpContext"/> dictionaries, so a policy's writes
    /// are already on the response by the time it is sent. Another transport can pass a
    /// <see cref="HeaderDictionary"/> of its own and do what it likes with what comes back.
    /// </remarks>
    public QueryResponse Execute(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders)
    {
        var drifted = request.Stamp is { } requestStamp && requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, request, services);
        try
        {
            var response = executor.Execute(request, data, new(services, requestHeaders, responseHeaders)) with
            {
                // Carried on every response, not only a drifted one: this is the signal a client uses
                // to notice drift in the first place, and it is the only such channel for a transport
                // that is not HTTP (which also advertises it as a response header).
                Stamp = schema.Stamp
            };

            // A drifted client may have been generated before an enum value rename, in which case the
            // payload carries names it does not know. The aliases let its reader resolve them; a
            // matching (or absent) stamp proves the client already has the current names, so nothing
            // rides along in the common case.
            if (drifted && schema.EnumAliases.Count > 0)
            {
                response = response with { EnumAliases = schema.EnumAliases };
            }

            recorder.Succeeded(response);
            return response;
        }
        // A rejected query from a client that was generated against a different model surface is far
        // more likely stale than hostile; say so, instead of leaving an unexplained rejection. A
        // matching stamp (or none) reports the plain validation message.
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }
    }

    /// <summary>Validates and executes a batch without a service provider (no DI-resolved policies).</summary>
    public QueryBatchResponse ExecuteBatch(QueryBatchRequest request, DbContext data) =>
        ExecuteBatch(request, data, EmptyServiceProvider.Instance);

    /// <summary>Validates and executes every entry of a batch, returning one result each.</summary>
    public QueryBatchResponse ExecuteBatch(QueryBatchRequest request, DbContext data, IServiceProvider services) =>
        ExecuteBatch(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>
    /// Validates and executes every entry of a batch. Entries are independent: each goes through the
    /// same validation, row policies, and telemetry a single query does, and one that is rejected or
    /// fails is reported in its own result rather than failing the batch.
    /// </summary>
    /// <remarks>
    /// Entries run sequentially against the one <see cref="DbContext"/> — which is not thread-safe, and
    /// which a batch has no reason to work around: what a batch saves is round-trips, not database
    /// time. It is not a transaction either, so an entry that fails leaves the entries before it
    /// answered. Only the batch envelope can fail the call: an unsupported wire version, or more
    /// entries than <see cref="ScryOptions.MaxBatchSize"/>, is rejected whole and before any entry runs.
    /// </remarks>
    public QueryBatchResponse ExecuteBatch(
        QueryBatchRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders)
    {
        if (request.Version > WireFormat.Version)
        {
            throw new ScryValidationException($"Unsupported wire version {request.Version}.");
        }

        if (request.Queries.Count > options.MaxBatchSize)
        {
            throw new ScryValidationException(
                $"The batch carries {request.Queries.Count} queries, more than the maximum of {options.MaxBatchSize}.");
        }

        using var activity = QueryRecorder.StartBatch(request.Queries.Count);

        var results = new List<QueryBatchResult>(request.Queries.Count);
        foreach (var query in request.Queries)
        {
            results.Add(ExecuteEntry(query, data, services, requestHeaders, responseHeaders));
        }

        return QueryBatchResponse.Create(results) with {Stamp = schema.Stamp};
    }

    // One entry, reported rather than thrown. The catches mirror the HTTP endpoint's: a validation
    // message is the client's own doing and is safe to return, and anything else is the fixed text a
    // 500 carries, so batching an entry never reveals more than sending it alone would.
    QueryBatchResult ExecuteEntry(
        QueryRequest query,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders)
    {
        try
        {
            return new()
            {
                Response = Execute(query, data, services, requestHeaders, responseHeaders)
            };
        }
        catch (ScryValidationException exception)
        {
            return new()
            {
                Error = exception.Message,
                Status = 400,
                StaleClient = exception.StaleClient
            };
        }
        catch (Exception)
        {
            return new()
            {
                Error = "Query execution failed.",
                Status = 500,
                // A drifted client faulting the server is far more likely stale than the server broken,
                // the same attribution the single-query endpoint makes for an execution failure.
                StaleClient = query.Stamp is { } stamp && stamp != schema.Stamp
            };
        }
    }

    /// <summary>
    /// Validates a request and returns its rows as a stream rather than a materialized result, plus the
    /// opening marker a transport writes before them.
    /// </summary>
    /// <remarks>
    /// Validation has run to completion by the time this returns — a rejected query never reaches EF —
    /// so a transport can commit to a success status before pulling the first row. A failure after that
    /// point is the provider's, and belongs in the stream's closing marker rather than a status code.
    /// </remarks>
    public (ScryStreamMarker Begin, IAsyncEnumerable<Dictionary<string, object?>> Rows) Stream(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        Cancel cancel = default) =>
        Stream(request, data, services, new HeaderDictionary(), new HeaderDictionary(), cancel);

    /// <summary>
    /// Streams a request, exposing <paramref name="requestHeaders"/> to row policies and letting them
    /// write to <paramref name="responseHeaders"/>.
    /// </summary>
    /// <remarks>
    /// Policies run while the query is built, which is before this returns — so a policy's writes are
    /// in hand while a transport can still send headers, rather than after the response has started.
    /// </remarks>
    public (ScryStreamMarker Begin, IAsyncEnumerable<Dictionary<string, object?>> Rows) Stream(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        Cancel cancel = default)
    {
        var drifted = request.Stamp is { } requestStamp && requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, request, services, streamed: true);
        QueryExecutor.RowSet rows;
        try
        {
            rows = executor.Stream(request, data, new(services, requestHeaders, responseHeaders));
        }
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }

        var begin = new ScryStreamMarker
        {
            Kind = ScryStream.Begin,
            Version = WireFormat.Version,
            Stamp = schema.Stamp,
            EnumAliases = drifted && schema.EnumAliases.Count > 0 ? schema.EnumAliases : null
        };

        return (begin, Shape(rows, options.MaxStreamRows, recorder, cancel));
    }

    static async IAsyncEnumerable<Dictionary<string, object?>> Shape(
        QueryExecutor.RowSet rows,
        int? maxRows,
        QueryRecorder recorder,
        [EnumeratorCancellation] Cancel cancel)
    {
        var count = 0;
        var enumerator = QueryExecutor.Enumerate(rows, cancel).GetAsyncEnumerator(cancel);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    recorder.Canceled(count);
                    throw;
                }
                catch (Exception exception)
                {
                    recorder.Failed(exception);
                    throw;
                }

                if (!moved)
                {
                    break;
                }

                if (count++ == maxRows)
                {
                    // Thrown rather than returned: the transport turns it into the stream's error marker,
                    // so the client sees a truncated result as a failure rather than as the end of the data.
                    var truncated = new ScryValidationException($"The query returned more than the maximum of {maxRows} streamed rows.");
                    recorder.Rejected(truncated);
                    throw truncated;
                }

                yield return QueryExecutor.ShapeRow(enumerator.Current, rows);
            }

            recorder.Succeeded(count);
        }
        finally
        {
            // A consumer that stops reading ends the stream here, with no completion of its own. The
            // first completion wins inside the recorder, so on every fully-reported path this no-ops.
            recorder.Canceled(count);
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>Streams a request without a service provider (no DI-resolved policies).</summary>
    public (ScryStreamMarker Begin, IAsyncEnumerable<Dictionary<string, object?>> Rows) Stream(
        QueryRequest request,
        DbContext data,
        Cancel cancel = default) =>
        Stream(request, data, EmptyServiceProvider.Instance, cancel);

    /// <summary>Executes a request without a service provider (no DI-resolved policies).</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data) =>
        Execute(request, data, EmptyServiceProvider.Instance);
}