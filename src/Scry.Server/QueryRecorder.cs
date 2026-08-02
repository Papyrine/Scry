/// <summary>
/// Telemetry for one query: an <see cref="Activity"/> spanning validation through shaping, the
/// duration and row-count metrics, and the <see cref="ScryAuditEntry"/> handed to every registered
/// <see cref="IScryAuditor"/>. All of it is pay-for-play — with no trace listener, no metrics
/// listener, and no auditor registered, a query pays two timestamps and a few null checks.
/// </summary>
sealed class QueryRecorder(QueryRequest request, IServiceProvider services, string source, bool streamed)
{
    static readonly string? version = typeof(QueryRecorder).Assembly.GetName().Version?.ToString();

    static readonly ActivitySource activitySource = new(ScryInstrumentation.ActivitySourceName, version);

    static readonly Meter meter = new(ScryInstrumentation.MeterName, version);

    // Buckets follow OTel's http.server.request.duration convention: seconds, weighted toward the
    // sub-second range a database-bound request lives in.
    static readonly Histogram<double> queryDuration = meter.CreateHistogram<double>(
        "scry.server.query.duration",
        unit: "s",
        description: "Duration of handling one query: validation, policies, execution, and shaping — for a stream, the whole read.",
        advice: new() { HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10] });

    static readonly Histogram<long> queryRows = meter.CreateHistogram<long>(
        "scry.server.query.rows",
        unit: "{row}",
        description: "Rows returned per successful query.",
        advice: new() { HistogramBucketBoundaries = [1, 10, 100, 1_000, 10_000, 100_000] });

    long started = Stopwatch.GetTimestamp();
    Activity? activity = StartActivity(source, request);
    bool completed;

    /// <summary>
    /// Starts the clock and, when something is listening, the activity. The source tag is the root
    /// name only when the schema knows it, so an arbitrary client string never becomes a tag value.
    /// </summary>
    public static QueryRecorder Start(Schema schema, QueryRequest request, IServiceProvider services, bool streamed = false) =>
        new(request, services, schema.TryGetSource(request.Root, out _) ? request.Root : "(unknown)", streamed);

    static Activity? StartActivity(string source, QueryRequest request)
    {
        var activity = activitySource.StartActivity($"scry.query {source}");
        activity?.SetTag("scry.source", source);
        activity?.SetTag("scry.operators", request.Pipeline.Count);
        return activity;
    }

    public void Succeeded(QueryResponse response) =>
        Complete(ScryQueryOutcome.Success, response.Kind, RowCount(response), error: null, staleClient: false);

    /// <summary>A streamed result, complete only once every row has been read.</summary>
    public void Succeeded(int rows) =>
        Complete(ScryQueryOutcome.Success, ResultKind.List, rows, error: null, staleClient: false);

    /// <summary>A buffered result written straight to the transport — kind and count arrive explicitly rather than being read off a payload.</summary>
    public void Succeeded(ResultKind kind, int rows) =>
        Complete(ScryQueryOutcome.Success, kind, rows, error: null, staleClient: false);

    public void Rejected(ScryValidationException exception) =>
        Complete(ScryQueryOutcome.Rejected, kind: null, rows: null, exception.Message, exception.StaleClient, exception);

    public void Failed(Exception exception)
    {
        // A policy filter is invoked through reflection, so its failure arrives wrapped — and the
        // wrapper's message says nothing. The telemetry exists to name the root cause.
        while (exception is TargetInvocationException { InnerException: { } inner })
        {
            exception = inner;
        }

        Complete(ScryQueryOutcome.Failed, kind: null, rows: null, exception.Message, staleClient: false, exception);
    }

    /// <summary>
    /// A streamed read that ended after <paramref name="rows"/> rows without reaching the end —
    /// canceled, or abandoned by its consumer. Also called on every stream's disposal as a backstop,
    /// which the first completion having won turns into a no-op.
    /// </summary>
    public void Canceled(int rows) =>
        Complete(ScryQueryOutcome.Canceled, kind: null, rows, "The stream ended before every row was read.", staleClient: false);

    void Complete(ScryQueryOutcome outcome, ResultKind? kind, int? rows, string? error, bool staleClient, Exception? exception = null)
    {
        if (completed)
        {
            return;
        }

        completed = true;
        var elapsed = Stopwatch.GetElapsedTime(started);
        var kindTag = KindTag(kind);

        var tags = new TagList
        {
            { "scry.source", source },
            { "scry.outcome", OutcomeTag(outcome) }
        };
        if (kindTag is not null)
        {
            tags.Add("scry.result_kind", kindTag);
        }

        if (exception is not null)
        {
            tags.Add("error.type", exception.GetType().FullName);
        }

        queryDuration.Record(elapsed.TotalSeconds, tags);

        if (outcome == ScryQueryOutcome.Success &&
            rows is { } returned &&
            kindTag is not null)
        {
            queryRows.Record(
                returned,
                new TagList
                {
                    { "scry.source", source },
                    { "scry.result_kind", kindTag }
                });
        }

        if (activity is not null)
        {
            if (kindTag is not null)
            {
                activity.SetTag("scry.result_kind", kindTag);
            }

            if (rows is { } count)
            {
                activity.SetTag("scry.rows", count);
            }

            if (staleClient)
            {
                activity.SetTag("scry.stale_client", true);
            }

            if (outcome != ScryQueryOutcome.Success)
            {
                activity.SetStatus(ActivityStatusCode.Error, error);
                if (exception is not null)
                {
                    activity.SetTag("error.type", exception.GetType().FullName);
                }
            }

            activity.Dispose();
        }

        Audit(outcome, kind, rows, error, staleClient, elapsed);
    }

    // Auditors run last, so one that throws cannot lose the measurements — and it is allowed to
    // throw: failing the request beats an audit trail that silently drops entries.
    void Audit(ScryQueryOutcome outcome, ResultKind? kind, int? rows, string? error, bool staleClient, TimeSpan elapsed)
    {
        // Resolved per query rather than at startup so an auditor can be scoped — reading the
        // current user off the request scope the HTTP endpoint passes in.
        if (services.GetService<IEnumerable<IScryAuditor>>() is not { } auditors)
        {
            return;
        }

        ScryAuditEntry? entry = null;
        foreach (var auditor in auditors)
        {
            entry ??= new(request, outcome, elapsed)
            {
                Kind = kind,
                Streamed = streamed,
                Rows = rows,
                Error = error,
                StaleClient = staleClient
            };
            auditor.Record(entry);
        }
    }

    // A stream folds its kind into the one tag value: it is list-shaped, but a row count means
    // something different when the rows were never buffered, so dashboards get to tell them apart.
    string? KindTag(ResultKind? kind)
    {
        if (streamed)
        {
            return "stream";
        }

        return kind switch
        {
            ResultKind.List => "list",
            ResultKind.Scalar => "scalar",
            ResultKind.Single => "single",
            ResultKind.Page => "page",
            _ => null
        };
    }

    static string OutcomeTag(ScryQueryOutcome outcome) =>
        outcome switch
        {
            ScryQueryOutcome.Success => "success",
            ScryQueryOutcome.Rejected => "rejected",
            ScryQueryOutcome.Failed => "failed",
            _ => "canceled"
        };

    /// <summary>
    /// The span covering a whole batch. Each entry starts its own activity inside it, so a batch reads
    /// as one parent with its queries nested rather than as unrelated siblings. Entries carry the
    /// metrics and audit entries; the batch adds only the grouping and its size.
    /// </summary>
    public static Activity? StartBatch(int size)
    {
        var activity = activitySource.StartActivity("scry.batch");
        activity?.SetTag("scry.batch.size", size);
        return activity;
    }

    /// <summary>
    /// A deserialization failure at the transport, before a request object exists to run a recorder
    /// over. Metric only: there is no request to audit and nothing worth a span of its own — but a
    /// client sending unparseable payloads is exactly what the outcome tag exists to make visible.
    /// </summary>
    public static void Malformed(TimeSpan elapsed) =>
        queryDuration.Record(
            elapsed.TotalSeconds,
            new TagList
            {
                { "scry.source", "(malformed)" },
                { "scry.outcome", "malformed" }
            });

    // The payload is already shaped, so the count is read off it rather than threaded through the
    // executor: an array's length for a list, the page envelope's items, presence for a single row.
    static int? RowCount(QueryResponse response) =>
        response.Kind switch
        {
            ResultKind.List => response.Payload.GetArrayLength(),
            ResultKind.Page => response.Payload.GetProperty("items").GetArrayLength(),
            ResultKind.Single => response.Payload.ValueKind == JsonValueKind.Null ? 0 : 1,
            _ => null
        };
}
