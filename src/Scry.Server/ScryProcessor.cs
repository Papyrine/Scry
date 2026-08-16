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
    SensitiveSchema sensitive;

    internal ScryProcessor(Schema schema, ScryOptions options)
    {
        this.schema = schema;
        this.options = options;
        executor = new(schema, options);
        sensitive = new(schema);
    }

    /// <summary>
    /// The name of a source whose rows depend on who asked — one carrying a row or attachment policy —
    /// or null where no source does. Read at startup to refuse a caching setup that would hand one
    /// caller's rows to the next.
    /// </summary>
    internal string? PolicedSource => schema.PolicedSource;

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
        var recorder = QueryRecorder.StartAttachment(schema, request, services);
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
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
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


    /// <summary>
    /// Applies what the model marks <c>[Sensitive]</c> to this request: refusing it where a constant
    /// compared against such a member arrived in a URL, and marking the response unstorable where one
    /// is returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves answer the two ways such a value escapes, and only the second is enforceable on
    /// its own. A URL is logged by every hop before it reaches here, so refusing it cannot unsay the
    /// first one — what refusing does is keep the answer from being cached under that URL, and make a
    /// client that got the choice wrong say so out loud rather than keep getting it wrong. The client
    /// reads the same rule off the same walk, so a request that reaches this is one whose sender was
    /// stale, hand-written, or lying.
    /// </para>
    /// <para>
    /// The message says only what to do. Naming the member would answer "which of these columns is the
    /// sensitive one?" for anyone willing to ask, and the attachment endpoint already collapses its own
    /// refusals for the same reason. What a developer needs is the analyzer, where the query is
    /// written.
    /// </para>
    /// </remarks>
    void ApplySensitivity(QueryRequest request, IHeaderDictionary responseHeaders, bool fromUrl)
    {
        var use = SensitiveWalk.Inspect(request, sensitive.IsSensitive);
        if (fromUrl && use.InConstant)
        {
            throw new ScryValidationException("This query compares a value against a member the model marks sensitive, so it must be sent as a request body rather than in a URL.")
            {
                RequiresBody = true
            };
        }

        // Not `private, no-cache`, which still stores: the rows are on the caller's disk either way and
        // outlive the session that asked for them. `no-store` is the only directive that says do not
        // keep this, and it is set here rather than at the endpoint because what is being returned is
        // not known until the request has been read.
        if (use.InProjection)
        {
            responseHeaders.CacheControl = "no-store";
        }
    }

    // The HTTP endpoints pass a collector so [BinaryTransfer] values leave as multipart parts; the
    // public overloads leave it null, so every non-HTTP consumer keeps today's inline base64.
    internal QueryResponse Execute(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary,
        bool fromUrl = false)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, request, services);
        try
        {
            ApplySensitivity(request, responseHeaders, fromUrl);
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Binary = binary,
                FromUrl = fromUrl
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
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
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
    /// but writes the result into <paramref name="output"/> as complete response bytes, returning null.
    /// The one result that does not go that way is the rare drifted-client envelope carrying the enum
    /// alias table, which is returned instead for the caller to serialize the general way. Rejections
    /// and failures throw exactly as <c>Execute</c> does.
    /// </summary>
    /// <remarks>
    /// <paramref name="spill"/> is what may let a large result stop being resident; null keeps the
    /// whole envelope buffered, which is what the batch's per-entry buffer needs.
    /// </remarks>
    internal async ValueTask<QueryResponse?> TryExecuteBufferedAsync(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        IBufferWriter<byte> output,
        ResponseSpill? spill = null,
        BinaryPartCollector? binary = null,
        Cancel cancel = default,
        bool fromUrl = false)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, request, services);
        try
        {
            ApplySensitivity(request, responseHeaders, fromUrl);
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Binary = binary
            };

            // The alias table is carried on the envelope only for a drifted client; that rare envelope keeps
            // the fully-general path rather than teaching the writer a second shape.
            if (drifted && schema.EnumAliases.Count > 0)
            {
                var fallback = executor.Execute(request, data, scope) with
                {
                    Stamp = schema.Stamp,
                    EnumAliases = schema.EnumAliases
                };
                recorder.Succeeded(fallback);
                return fallback;
            }

            var (kind, rows) = await executor.ExecuteBufferedAsync(request, data, scope, schema.Stamp, output, spill, cancel);
            recorder.Succeeded(kind, rows);
            return null;
        }
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
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
        catch (OperationCanceledException)
        {
            // Reading the rows asynchronously is what makes a client disconnect land here rather than
            // at the final write, and an abandoned request is not a query that failed. Ahead of the
            // catch below so it is not counted as one.
            recorder.Canceled();
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
        RejectUnusableBatch(request);

        using var activity = QueryRecorder.StartBatch(request.Queries.Count);

        var results = new List<QueryBatchResult>(request.Queries.Count);
        foreach (var query in request.Queries)
        {
            results.Add(ExecuteEntry(query, data, services, requestHeaders, responseHeaders, binary));
        }

        return QueryBatchResponse.Create(results) with {Stamp = schema.Stamp};
    }

    /// <summary>
    /// Executes a batch like <see cref="ExecuteBatch(QueryBatchRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary)"/>,
    /// but writes the whole envelope into <paramref name="output"/> — every entry that is rows written
    /// straight from the projected values rather than through dictionaries and a
    /// <see cref="JsonElement"/> that the envelope around it would then serialize a second time.
    /// Byte-identical to serializing what <c>ExecuteBatch</c> returns, which the golden tests pin.
    /// </summary>
    /// <remarks>
    /// Only an envelope failure throws; a rejected or failed entry is written as its own result exactly
    /// as <c>ExecuteBatch</c> reports one.
    /// </remarks>
    internal async Task ExecuteBatchBufferedAsync(
        QueryBatchRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        IBufferWriter<byte> output,
        BinaryPartCollector? binary,
        ResponseSpill? spill = null,
        Cancel cancel = default)
    {
        RejectUnusableBatch(request);

        using var activity = QueryRecorder.StartBatch(request.Queries.Count);

        // Granted once, before the first entry runs — the only point at which a batch can decide. Its
        // parts are numbered globally and its envelope arrives last, so an entry that drained would be
        // betting that no later entry produces a part the drained bytes should have preceded. Only a
        // model with no binary member anywhere makes that bet safe.
        spill?.AllowSpill(!schema.CarriesBinary);

        // One scratch buffer for the whole batch, reset per entry rather than rented per entry, so a
        // batch of n entries rents once and settles at the width of its largest.
        using var entry = new PooledBufferWriter();
        await using var json = new Utf8JsonWriter(output);
        ResponseWriter.BeginBatch(json);
        foreach (var query in request.Queries)
        {
            await WriteEntryAsync(json, entry, query, data, services, requestHeaders, responseHeaders, binary, cancel);

            // Between entries, never inside one: an entry is written to a buffer of its own and inserted
            // whole precisely so a failure part-way through its rows is still reported as that entry's
            // own result, which nothing already on the wire could be replaced by.
            if (spill?.ShouldDrain(json.BytesPending) == true)
            {
                await json.FlushAsync(cancel);
                await spill.DrainAsync(cancel);
            }
        }

        ResponseWriter.EndBatch(json, schema.Stamp);
        await json.FlushAsync(cancel);
    }

    // The envelope-level rejections, which are the only way a batch fails as a whole: they are checked
    // before any entry runs, so a rejected batch has executed nothing.
    void RejectUnusableBatch(QueryBatchRequest request)
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
    }

    // One entry, written rather than returned — the buffered counterpart of ExecuteEntry, and its
    // catches must stay identical to that one's.
    //
    // The entry is written into a buffer of its own first and only inserted once it is whole: an entry
    // can fail part-way through its rows (the database is read as they are written), and a writer
    // already mid-array cannot take back what it has written to report the failure in its place.
    async Task WriteEntryAsync(
        Utf8JsonWriter json,
        PooledBufferWriter entry,
        QueryRequest query,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary,
        Cancel cancel)
    {
        entry.Reset();

        QueryResponse? fallback;
        try
        {
            // No spill: an entry is inserted into the envelope only once it is whole, which is the
            // whole reason it is written into a buffer of its own.
            fallback = await TryExecuteBufferedAsync(
                query,
                data,
                services,
                requestHeaders,
                responseHeaders,
                entry,
                spill: null,
                binary,
                cancel);
        }
        catch (ScryValidationException exception)
        {
            ResponseWriter.WriteEntry(json, exception.Message, 400, exception.StaleClient);
            return;
        }
        catch (OperationCanceledException)
        {
            // The one place this diverges from ExecuteEntry, which reads its rows synchronously and so
            // cannot be abandoned part-way. Nobody is left to read a per-entry failure, and the entries
            // after this one have nobody to answer either, so the batch goes with it.
            throw;
        }
        catch (Exception)
        {
            // A drifted client faulting the server is far more likely stale than the server broken,
            // the same attribution the single-query endpoint makes for an execution failure.
            ResponseWriter.WriteEntry(
                json,
                "Query execution failed.",
                500,
                query.Stamp is { } stamp && stamp != schema.Stamp);
            return;
        }

        if (fallback is null)
        {
            ResponseWriter.WriteEntry(json, entry.WrittenMemory.Span);
            return;
        }

        ResponseWriter.WriteEntry(json, fallback);
    }

    // One entry, reported rather than thrown. The catches mirror the HTTP endpoint's: a validation
    // message is the client's own doing and is safe to return, and anything else is the fixed text a
    // 500 carries, so batching an entry never reveals more than sending it alone would. WriteEntry
    // above is the buffered counterpart and must report an entry exactly as this does.
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
        var recorder = QueryRecorder.Start(schema, request, services, streamed: true);
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
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
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
