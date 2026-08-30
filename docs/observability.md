# Observability

The server reports what it was asked and what it did through three independent channels — traces, metrics, and an audit hook — all emitted from `ScryProcessor`, the single choke point every transport goes through. Each channel is off until something subscribes: with no trace listener, no metrics listener, and no registered auditor, a query pays two timestamps and a few null checks. Nothing is emitted client-side.


## Wiring OpenTelemetry

The names are constants on `ScryInstrumentation`; this is the [sample server](sample.md)'s registration:

<!-- snippet: openTelemetry -->
<a id='snippet-openTelemetry'></a>
```cs
builder.Services.AddOpenTelemetry()
    .WithTracing(_ => _.AddSource(ScryInstrumentation.ActivitySourceName))
    .WithMetrics(_ => _.AddMeter(ScryInstrumentation.MeterName));
```
<sup><a href='/samples/Sample.Server/Program.cs#L74-L78' title='Snippet source file'>snippet source</a> | <a href='#snippet-openTelemetry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Nothing in Scry depends on OpenTelemetry — the traces are a plain `ActivitySource` and the metrics a plain `Meter`, so any `ActivityListener`/`MeterListener`-based collector works the same way.


## Traces

One activity per query, named `scry.query {source}`, spanning validation through shaping — and, for a streamed query, the whole read. It parents onto whatever activity is current, so on the HTTP endpoint it nests under ASP.NET Core's request span.

| Tag | Value |
| --- | --- |
| `scry.source` | The root source name — or `(unknown)` when the root is not in the schema, so an arbitrary client string never becomes a tag value. |
| `scry.operators` | The pipeline length. |
| `scry.result_kind` | `list`, `scalar`, `single`, `page`, or `stream`; absent when the query never produced a result. |
| `scry.rows` | Rows delivered, where rows are the result. |
| `scry.stale_client` | `true` when a rejection was attributed to a stale client ([schema versioning](schema-versioning.md)). |
| `error.type` | The exception type, on any non-success. |

A rejection or failure additionally sets the activity's status to error, carrying the same message the outcome does.

A [batch](batching.md) adds one `scry.batch` span, tagged `scry.batch.size`, with its entries' activities nested under it — so batched queries read as one unit of work rather than as unrelated siblings. The entries carry the metrics and audit entries; the batch itself carries neither.


## Metrics

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `scry.server.query.duration` | histogram | `s` | `scry.source`, `scry.outcome`, `scry.result_kind` (success only), `error.type` (failures only) |
| `scry.server.query.rows` | histogram | `{row}` | `scry.source`, `scry.result_kind` |

Every query records a duration, whatever its outcome, so query counts come off the duration histogram's count. The rows histogram records successful queries only. `scry.outcome` is one of:

| Value | Meaning |
| --- | --- |
| `success` | Validated, executed, every row delivered. |
| `rejected` | Refused by validation — or a stream truncated by `MaxStreamRows`. |
| `failed` | Validation passed; execution threw. |
| `canceled` | A streamed read that ended before the last row: canceled, or its consumer stopped reading. |
| `denied` | A [row policy denied a row](policies.md#what-a-denied-row-produces) the query read, and reports denials rather than hiding them. |
| `malformed` | The HTTP body failed to deserialize; the request never reached the processor. |

`denied` is counted apart from `rejected` because nothing about the query was wrong: it asked for rows this caller may not have. It is also the mode that discloses their existence, so a rate worth watching — a caller driving it up is mapping what it cannot see.

A `rejected` rate that deployments do not explain is the signal worth alerting on. A generated client cannot produce an invalid request, so rejections are either stale clients — benign, marked by `scry.stale_client` and a `staleClient` audit entry, expected to spike right after a model change ships — or requests written by hand, which is probing. `malformed` is the same signal one layer earlier.


## The audit hook

For a per-query record with the full request — the level of detail metrics deliberately do not carry — register an `IScryAuditor`:

<!-- snippet: auditorInterface -->
<a id='snippet-auditorInterface'></a>
```cs
public interface IScryAuditor
{
    /// <summary>Called once per query, after it completes. See <see cref="ScryAuditEntry"/>.</summary>
    void Record(ScryAuditEntry entry);
}
```
<sup><a href='/src/Scry.Server/IScryAuditor.cs#L14-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-auditorInterface' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

```cs
services.AddScoped<IScryAuditor, QueryAuditor>();
```

Every registered auditor is called once per query with a `ScryAuditEntry`:

<!-- snippet: auditEntry -->
<a id='snippet-auditEntry'></a>
```cs
public sealed record ScryAuditEntry(
    QueryRequest? Request,
    ScryQueryOutcome Outcome,
    TimeSpan Duration)
{
    /// <summary>
    /// The attachment fetched, when the entry describes one rather than a query: which member of which
    /// source, and the row key it was asked for. Null for a query.
    /// </summary>
    /// <remarks>
    /// Worth watching on its own. An attachment is reached by row key through an endpoint of its own,
    /// so a run of rejected or not-found fetches is what key-guessing looks like.
    /// </remarks>
    public AttachmentRequest? Attachment { get; init; }

    /// <summary>The result shape, when the query succeeded; null when it never produced one.</summary>
    public ResultKind? Kind { get; init; }

    /// <summary>Whether the rows were streamed rather than materialized into a response.</summary>
    public bool Streamed { get; init; }

    /// <summary>
    /// Rows delivered: a list or page's count, 0 or 1 for a single row, the rows read for a stream —
    /// including one that ended early. Null where rows are not the result (a scalar) or the query
    /// never ran.
    /// </summary>
    public int? Rows { get; init; }

    /// <summary>The rejection or failure message; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// True when a rejection was attributed to a stale client (a schema stamp differing from the
    /// server's) rather than an invalid query — the benign explanation. A rejection without it is
    /// the one worth watching.
    /// </summary>
    public bool StaleClient { get; init; }
}
```
<sup><a href='/src/Scry.Server/ScryAuditEntry.cs#L18-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-auditEntry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Semantics:

- **Resolution is per query, from the calling scope.** On the HTTP endpoint that is the request's scope, so a scoped auditor can read the current user. Any number of auditors can be registered; none means nothing is recorded.
- **Auditors fail closed.** An auditor that throws fails the request — an audit trail that silently drops entries is worse than a failed query. An implementation that must not block should hand the entry to a queue and return.
- **`Error` is the real message.** For a `Failed` outcome the client saw a generic 500; the entry carries the underlying exception's message. The audit trail is where execution failures are readable.
- **Streams are recorded at completion**, with the rows actually delivered — including a `Canceled` entry when the read stopped partway.
- **A batch is audited per entry, not per request.** The trail records what was asked, and a [batch](batching.md) asked more than once; there is no entry for the batch itself.
- **Malformed bodies are not audited.** A payload that fails deserialization never becomes a request object, so it appears in metrics only.


## Hosting without HTTP

All three channels live in `ScryProcessor`, so a [non-HTTP host](server.md#hosting-without-the-http-endpoint) gets them unchanged: the auditors resolve from whatever `IServiceProvider` the call passes, and the activity parents onto whatever the transport has current. The one HTTP-only piece is the `malformed` outcome, recorded by the endpoint for bodies the processor never sees.
