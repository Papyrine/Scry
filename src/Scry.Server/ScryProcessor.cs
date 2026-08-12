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

    /// <summary>
    /// The client's fingerprint of the body it sent, when it sent one. Recorded in telemetry and nothing
    /// else: it is attacker-controlled, so it identifies the request only as far as the client is honest
    /// — see <see cref="QueryFingerprint"/> for what it may and may not be used for. A transport that
    /// carries no headers simply reports none.
    /// </summary>
    static string? Fingerprint(IHeaderDictionary headers) =>
        headers.TryGetValue(WireFormat.QueryHashHeader, out var value)
            ? QueryFingerprint.TryRead(value.ToString())
            : null;

    /// <summary>Validates and executes a request, returning the shaped result.</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data, IServiceProvider services) =>
        Execute(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>Fetches one attachment's bytes, authorized by the source's attachment policy.</summary>
    public ScryAttachmentResult FetchAttachment(AttachmentRequest request, DbContext data, IServiceProvider services) =>
        FetchAttachment(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>
    /// Fetches one attachment's bytes, exposing <paramref name="requestHeaders"/> to the attachment
    /// policy and letting it write to <paramref name="responseHeaders"/>.
    /// </summary>
    /// <remarks>
    /// The choke point for the attachment endpoint, as <see cref="Execute(QueryRequest, DbContext, IServiceProvider)"/>
    /// is for queries: the policy runs here, the row is read through its source's row policies, and the
    /// fetch is audited — so another transport gets all three by calling this rather than reaching for
    /// the database itself. A refusal is not distinguished from a missing row; see
    /// <see cref="ScryAttachmentResult"/>.
    /// </remarks>
    public ScryAttachmentResult FetchAttachment(
        AttachmentRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.StartAttachment(schema, request, services, Fingerprint(requestHeaders));
        try
        {
            var scope = new CallScope(services, requestHeaders, responseHeaders);
            var result = executor.FetchAttachment(request, data, scope);

            // Rows are 1 for a value handed over and 0 for everything withheld, which keeps a run of
            // refusals visible in the metrics without saying which kind of refusal it was.
            recorder.Succeeded(ResultKind.Single, result.Found ? 1 : 0);
            return result;
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
    }

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
        IHeaderDictionary responseHeaders) =>
        Execute(request, data, services, requestHeaders, responseHeaders, binary: null);

    // The HTTP endpoints pass a collector so [BinaryTransfer] values leave as multipart parts; the
    // public overloads leave it null, so every non-HTTP consumer keeps today's inline base64.
    internal QueryResponse Execute(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, request, services, Fingerprint(requestHeaders));
        try
        {
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Binary = binary
            };
            var response = executor.Execute(request, data, scope) with
            {
                // Carried on every response, not only a drifted one: this is the signal a client uses
                // to notice drift in the first place, and it is the only such channel for a transport
                // that is not HTTP (which also advertises it as a response header).
                Stamp = schema.Stamp
            };

            // A drifted client may have been generated before an enum value rename, in which case the
            // payload carries names it does not know. The aliases let its reader resolve them; a
            // matching (or absent) stamp proves the client already has the current names, so nothing
            // is sent in the common case.
            if (drifted && schema.EnumAliases.Count > 0)
            {
                response = response with
                {
                    EnumAliases = schema.EnumAliases
                };
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

    /// <summary>
    /// Executes like <see cref="Execute(QueryRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary)"/>,
    /// but a list result is written into <paramref name="output"/> as complete response bytes (true).
    /// Any other result — a terminal's response, or the rare drifted-client envelope that carries the
    /// enum alias table — comes back as <paramref name="fallback"/> (false) for the caller to
    /// serialize the general way. Rejections and failures throw exactly as <c>Execute</c> does.
    /// </summary>
    internal bool TryExecuteBuffered(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        IBufferWriter<byte> output,
        out QueryResponse? fallback,
        BinaryPartCollector? binary = null)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, request, services, Fingerprint(requestHeaders));
        try
        {
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Binary = binary
            };

            // The alias table is carried on the envelope only for a drifted client; that rare envelope keeps
            // the fully-general path rather than teaching the writer a second shape.
            if (drifted && schema.EnumAliases.Count > 0)
            {
                fallback = executor.Execute(request, data, scope) with
                {
                    Stamp = schema.Stamp,
                    EnumAliases = schema.EnumAliases
                };
                recorder.Succeeded(fallback);
                return false;
            }

            if (executor.ExecuteBuffered(request, data, scope, schema.Stamp, output, out var kind, out var rows) is { } complete)
            {
                fallback = complete with
                {
                    Stamp = schema.Stamp
                };
                recorder.Succeeded(fallback);
                return false;
            }

            recorder.Succeeded(kind, rows);
            fallback = null;
            return true;
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
    }

    /// <summary>
    /// The SQL a request would run, without running it. Resolves the <see cref="DbContext"/> from
    /// <paramref name="services"/>.
    /// </summary>
    public string ToQueryString(QueryRequest request, IServiceProvider services) =>
        ToQueryString(request, (DbContext)services.GetRequiredService(options.ContextType), services);

    /// <summary>The SQL a request would run, without running it.</summary>
    public string ToQueryString(QueryRequest request, DbContext data, IServiceProvider services) =>
        ToQueryString(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>
    /// Validates a request, applies its row policies, rebinds it onto EF — then reads back the SQL
    /// instead of executing it. Everything a query is subject to has already happened, so the SQL shown
    /// is the SQL that would run, policy predicates included, and no request survives here that would
    /// have been rejected as a query.
    /// </summary>
    /// <remarks>
    /// This is a debugging aid, and the SQL reveals more than a result does — real table and column
    /// names, and the shape of any <see cref="IReturnablePolicy{T}"/> that narrowed the query. Treat it
    /// as privileged: the explorer keeps it behind a Development-only guard of its own.
    /// <para>
    /// Only a row-returning query has SQL to show. A terminal that folds the rows to a value is
    /// answered by executing it, so one is refused rather than run.
    /// </para>
    /// </remarks>
    public string ToQueryString(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders)
    {
        var rows = executor.Build(request, data, new(services, requestHeaders, responseHeaders));

        // Checked before asking, not after: EF's ToQueryString decides by executing the query and
        // inspecting what comes back, and for an in-memory source that means actually running it. It
        // then reports the mismatch by *returning* an explanatory sentence rather than throwing, which
        // would otherwise be handed back as though it were SQL.
        if (rows.Rows.Provider is not IAsyncQueryProvider)
        {
            throw new ScryValidationException(
                $"No SQL is available for source '{request.Root}': it is not backed by the database (a [QueryablePoco] source is supplied in memory).");
        }

        return rows.Rows.ToQueryString();
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
        IHeaderDictionary responseHeaders) =>
        ExecuteBatch(request, data, services, requestHeaders, responseHeaders, binary: null);

    // One collector threads through every entry, which is what numbers a batch's parts globally.
    internal QueryBatchResponse ExecuteBatch(
        QueryBatchRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary)
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
            results.Add(ExecuteEntry(query, data, services, requestHeaders, responseHeaders, binary));
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
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary)
    {
        try
        {
            return new()
            {
                Response = Execute(query, data, services, requestHeaders, responseHeaders, binary)
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
        var (begin, rows, recorder) = StreamCore(request, data, services, requestHeaders, responseHeaders);
        return (begin, Shape(rows, options.MaxStreamRows, recorder, cancel));
    }

    /// <summary>
    /// Streams like <see cref="Stream(QueryRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary, Cancel)"/>,
    /// but each row arrives as its finished JSON bytes, written by the plan's shape writer — the
    /// buffer is valid until the next row is pulled.
    /// </summary>
    internal (ScryStreamMarker Begin, bool Binary, IAsyncEnumerable<ReadOnlyMemory<byte>> Rows) StreamBuffered(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        Cancel cancel = default,
        BinaryPartCollector? binary = null)
    {
        var (begin, rows, recorder) = StreamCore(request, data, services, requestHeaders, responseHeaders, binary);
        // Whether any row can divert — known from the plan before the first byte, which is what lets
        // the transport commit to a multipart content type up front, data-independently.
        var diverting = binary is not null && rows.Plan.BinarySlots is not null;
        return (begin, diverting, Lines(rows, options.MaxStreamRows, recorder, cancel));
    }

    (ScryStreamMarker Begin, QueryExecutor.RowSet Rows, QueryRecorder Recorder) StreamCore(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary = null)
    {
        var drifted = request.Stamp is { } requestStamp && requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, request, services, Fingerprint(requestHeaders), streamed: true);
        QueryExecutor.RowSet rows;
        try
        {
            rows = executor.Stream(request, data, new(services, requestHeaders, responseHeaders)
            {
                Binary = binary
            });
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

        return (begin, rows, recorder);
    }

    static async IAsyncEnumerable<Dictionary<string, object?>> Shape(
        QueryExecutor.RowSet rows,
        int? maxRows,
        QueryRecorder recorder,
        [EnumeratorCancellation] Cancel cancel)
    {
        await foreach (var row in Raw(rows, maxRows, recorder, cancel))
        {
            yield return QueryExecutor.ShapeRow(row, rows);
        }
    }

    // One writer and one buffer serve the whole stream: each row overwrites the last, which is why
    // the yielded memory is only valid until the next pull — exactly how the transport consumes it.
    // The buffer is pooled, so the memory is also only valid until the enumeration ends, which is the
    // same moment by the time the transport has written the row out.
    static async IAsyncEnumerable<ReadOnlyMemory<byte>> Lines(
        QueryExecutor.RowSet rows,
        int? maxRows,
        QueryRecorder recorder,
        [EnumeratorCancellation] Cancel cancel)
    {
        var writer = rows.Plan.Writer;
        var buffer = new PooledBufferWriter();
        Utf8JsonWriter? json = null;
        try
        {
            await foreach (var row in Raw(rows, maxRows, recorder, cancel))
            {
                buffer.Reset();
                if (json is null)
                {
                    json = new(buffer);
                }
                else
                {
                    json.Reset(buffer);
                }

                writer.WriteRow(json, ResponseWriter.Row(row, rows), rows.Binary);
                await json.FlushAsync(cancel);
                yield return buffer.WrittenMemory;
            }
        }
        finally
        {
            if (json != null)
            {
                await json.DisposeAsync();
            }

            buffer.Dispose();
        }
    }

    static async IAsyncEnumerable<object> Raw(
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

                yield return enumerator.Current;
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

    /// <summary>
    /// Fetches an attachment without a service provider. The attachment policy still runs — it is
    /// constructed directly when DI has no answer — so this is unauthorized only if the policy is.
    /// </summary>
    public ScryAttachmentResult FetchAttachment(AttachmentRequest request, DbContext data) =>
        FetchAttachment(request, data, EmptyServiceProvider.Instance);
}
