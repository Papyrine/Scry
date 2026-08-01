# Server

`Scry.Server` validates an incoming query AST against the allow-list, rebinds it onto the real EF Core entity types, applies row policies, executes it against a `DbContext`, and returns the projected rows.


## Registration

<!-- snippet: serverRegistration -->
<a id='snippet-serverRegistration'></a>
```cs
builder.Services
    .AddScry<SampleContext>(
    _ =>
    {
        // Holiday is a [QueryablePoco]: it has no table, so the server supplies its rows. Every
        // [QueryablePoco] type must be registered here or AddScry throws at startup.
        _.AddPocoSource(_ => Holiday.Seed());
        _.MaxPageSize = 200;
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L26-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-serverRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddPocoSource` registers the data for a `[QueryablePoco]` type — see [POCO sources](#poco-sources) below. `MaxPageSize` is one of the [limits](#options).

`AddScry<TContext>`:

- Scans `typeof(TContext).Assembly` for types carrying `[Queryable]`, `[QueryableView]`, or `[QueryablePoco]`.
- Builds the allow-list schema **once**, at registration time.
- Registers the `ScryOptions` and a `ScryProcessor` as singletons.

The `DbContext` itself is resolved per request from DI, so the usual `AddDbContext` scoping applies.

Failures surface at startup, not at first request:

- A `[QueryablePoco]` type with no registered data.
- Two sources resolving to the same name.


## Mapping the endpoint

<!-- snippet: mapScry -->
<a id='snippet-mapScry'></a>
```cs
app.MapScry("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L51-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Three `POST` endpoints, from the one call:

| Route | Request | Response |
| --- | --- | --- |
| the pattern given | [`QueryRequest`](wire-format.md) | one `QueryResponse` |
| `…/stream` | the same `QueryRequest` | [newline-delimited rows](wire-format.md#streamed-results), for [`ToAsyncEnumerable`](querying.md#streaming-rows) |
| `…/batch` | [`QueryBatchRequest`](wire-format.md#batched-queries) | one result per entry, for [batching](batching.md) |

They are mapped together deliberately. Streaming reads the same query surface a row at a time and batching carries several of its queries at once; neither widens what can be asked, so opting into them separately would only invite deployments where one is protected and the others are not. `MapScry` returns an `IEndpointConventionBuilder` covering **all three**, so the usual conventions apply once:

```cs
app.MapScry("/api/query")
    .RequireAuthorization()
    .RequireCors("client");
```

Authentication and authorization are **not** Scry's job — put them on the endpoint. See [Security model](security.md).


## Options

<!-- snippet: scryOptionsLimits -->
<a id='snippet-scryOptionsLimits'></a>
```cs
/// <summary>Maximum number of rows a single query may request via <c>Take</c>. Default 1000.</summary>
public int MaxPageSize { get; set; } = 1000;

/// <summary>
/// Page size applied to a paged query (<c>ToPageAsync</c>) that does not request one. Bounds an
/// otherwise-unbounded page; the effective size is always capped by <see cref="MaxPageSize"/>. Default 100.
/// </summary>
public int DefaultPageSize { get; set; } = 100;

/// <summary>Maximum navigation-path length allowed in a member expression. Default 4.</summary>
public int MaxNavigationDepth { get; set; } = 4;

/// <summary>Maximum number of operators in a query pipeline. Default 32.</summary>
public int MaxPipelineLength { get; set; } = 32;

/// <summary>Maximum expression nesting depth in a predicate. Default 32.</summary>
public int MaxExpressionDepth { get; set; } = 32;

/// <summary>
/// Maximum number of values a client may supply to a set-membership test (<c>Contains</c>, which
/// becomes a SQL <c>IN</c>). Default 1000.
/// </summary>
public int MaxInValues { get; set; } = 1000;

/// <summary>
/// Maximum number of queries one batch request may carry. Default 20.
/// </summary>
/// <remarks>
/// A batch is the one place a single request costs more than one query, so this is the bound that
/// keeps it from being an amplifier: every other limit is per query and would otherwise apply to an
/// arbitrary number of them. A batch over the limit is rejected whole, before any entry runs.
/// </remarks>
public int MaxBatchSize { get; set; } = 20;

/// <summary>
/// Maximum number of rows a streamed query may return, or null — the default — for no limit.
/// </summary>
/// <remarks>
/// Null matches <c>ToListAsync</c>, which has never been bounded either: <see cref="MaxPageSize"/>
/// caps <c>Take</c> and a page, not an unbounded enumeration. Streaming is the safer of the two
/// server-side, since the rows are never buffered — but it holds a connection and a response open
/// for as long as the client reads, which is the reason to offer a bound at all. A stream that
/// reaches the limit ends with an error marker rather than a short result, so a client cannot
/// mistake truncation for the end of the data.
/// </remarks>
public int? MaxStreamRows { get; set; }
```
<sup><a href='/src/Scry.Server/ScryOptions.cs#L9-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-scryOptionsLimits' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Every limit is enforced during validation, before any expression is rebound or executed.

| Option | Default | Rejects |
| --- | --- | --- |
| `MaxPageSize` | 1000 | `Take n` where `n` exceeds it. Note this caps an explicit `Take`; it does not implicitly page an unbounded query. |
| `MaxNavigationDepth` | 4 | Member paths longer than the limit, and projection nesting deeper than it. |
| `MaxPipelineLength` | 32 | Pipelines with more operators than the limit. |
| `MaxExpressionDepth` | 32 | Predicate/expression trees nested deeper than the limit. |
| `MaxInValues` | 1000 | `Contains` over a client-supplied set larger than the limit. Bounds the SQL `IN` list a single request can build. |
| `MaxBatchSize` | 20 | A [batch](batching.md) carrying more queries than the limit — rejected whole, before any entry runs. Every other limit is per query, so this is what stops one request from costing an arbitrary number of them. |
| `MaxStreamRows` | unset | Nothing by default. Set it to cap a [streamed](querying.md#streaming-rows) result; the stream then ends with an error marker rather than a short one. Like `MaxPageSize`, it does not implicitly bound an unbounded query — it bounds one that asked to stream. |

`CaseSensitiveCollation` and `CaseInsensitiveCollation` are not limits but capabilities: both default to null, which rejects a request asking for that case sensitivity. Set them to collations the database has (`Latin1_General_CS_AS`, `Latin1_General_CI_AS` on SQL Server) to enable [case-sensitive matching](querying.md#operators-1). They are server settings because a collation is emitted into the SQL text rather than parameterized, so accepting one from a request would be the only place an attacker-supplied string reached SQL as anything but a parameter.

Treat them like a connection string: **trusted configuration, never a value taken from a request** or from anywhere a caller can influence. A request cannot carry a collation — the wire names a case sensitivity and the string is looked up here — so the only remaining path is a deployment wiring the option up from somewhere it does not control. Both are checked at startup and must be plain names (letters, digits, underscores); a provider does quote the name as well, but that is provider-overridable behaviour rather than a guarantee.

Applying a collation also costs an index. `WHERE col COLLATE X = @p` is not SARGable, so an index built under the column's own collation cannot be seeked and the query degrades to a scan. Where a whole column should be matched one way, setting the **column's** collation is both faster and simpler than asking per query.


## POCO sources

A `[QueryablePoco]` type is not in the EF model, so the server supplies its data.

Fixed collection:

```cs
options.AddPocoSource<Holiday>(Holiday.Seed());
```

Resolved per request from the service provider:

```cs
options.AddPocoSource<Holiday>(services =>
    services.GetRequiredService<IHolidayFeed>().Current());
```

The registered sequence is wrapped with `AsQueryable()`, so the pipeline runs in memory over LINQ to<!-- include: poco-in-memory. path: /docs/includes/poco-in-memory.include.md -->
Objects with the same validation, shaping, and limits as a database source.<!-- endInclude -->


## Row policies

```cs
options.AddPolicy<Employee, TenantPolicy>();
```

An `IReturnablePolicy<T>` is applied to the source before any client operator, so client filters can<!-- include: policy-ordering. path: /docs/includes/policy-ordering.include.md -->
only narrow an already-authorized set.<!-- endInclude -->

It overrides `[ReturnableWith]` on the same type. See [Row policies](policies.md).


## Hosting without the HTTP endpoint

`ScryProcessor` is the programmatic entry point that `MapScry` wraps. Use it directly for a different transport (SignalR, gRPC, a message queue) or in tests. `ScryProcessor.Create<TContext>` takes the same configuration delegate as `AddScry`:

<!-- snippet: processorCreate -->
<a id='snippet-processorCreate'></a>
```cs
public static ScryProcessor Instance { get; } = ScryProcessor.Create<TestContext>(
    options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
```
<sup><a href='/src/Scry.Tests/SharedProcessor.cs#L9-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-processorCreate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Execute a request against any `DbContext` instance:

```cs
var response = processor.Execute(request, dbContext);
```

There are three `Execute` overloads: one taking an `IServiceProvider` (used to resolve policies), one without — which falls back to activating policies via their parameterless constructor — and one that additionally takes a request and a response `IHeaderDictionary`:

```cs
var responseHeaders = new HeaderDictionary();
var response = processor.Execute(request, dbContext, services, requestHeaders, responseHeaders);
```

Those reach [row policies](policies.md#reading-and-writing-headers) as `ScryPolicyContext.RequestHeaders` and `ResponseHeaders`. `MapScry` passes the live `HttpContext` dictionaries; a transport of its own supplies whatever it has, or nothing — the shorter overloads pass empty dictionaries, so a policy that reads a header off the HTTP endpoint gets nothing rather than faulting. `Stream` mirrors the same pair.

Note this is the only channel for headers: they are not part of the [wire request](wire-format.md), so the [per-query header operators](querying.md#headers) on the client work over HTTP alone and refuse a query sent through a custom transport delegate rather than dropping what they were asked to send.

`processor.Describe()` returns the [introspection](explorer.md#introspection) contract.

Because `ScryClient` takes an arbitrary transport delegate, the same processor also supports an in-process client — the whole pipeline, LINQ to rows, with no web host:

<!-- snippet: inProcessClient -->
<a id='snippet-inProcessClient'></a>
```cs
static ScryClient ClientFor(TestContext context) =>
    new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
```
<sup><a href='/src/Scry.Tests/ClientRoundTripTests.cs#L368-L371' title='Snippet source file'>snippet source</a> | <a href='#snippet-inProcessClient' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Execution pipeline

For each request the server, in order:

1. **Validates** the whole AST against the schema and the limits. Any violation throws `ScryValidationException` and nothing else runs.
2. **Resolves** the source — `DbContext.Set<T>()` for entities and views, the registered factory for POCOs.
3. **Applies the row policy**, if the source has one.
4. **Rebinds** each operator onto real CLR expression trees over the entity type. CLR types come only from the schema, never from the wire.
5. **Executes** through the underlying provider — EF Core translates to SQL for entities and views.
6. **Shapes** the result: a `Select` to `object[]` of the requested leaves, folded back into (possibly nested) JSON objects using the projection plan.

Because the projection is applied inside the query, only the requested columns are read from the database.

Terminal handling:

- `Count` / `Any` execute as scalars.
- A predicate on `First` / `Single` is applied **before** the projection.
- `First` / `Single` return a single object, or `null` for the `OrDefault` variants over an empty sequence.


## Error handling

The endpoint maps failures deliberately:

| Cause | Status | Body |
| --- | --- | --- |
| Malformed JSON, unknown discriminator, wrong shape (`ScryWireException`) | `400` | `{"error":"..."}` |
| Allow-list or limit violation (`ScryValidationException`) | `400` | `{"error":"..."}` |
| Anything else | `500` | `{"error":"Query execution failed."}` |

When the request's [schema stamp](schema-versioning.md#the-two-version-axes) differs from the server's, the `400` and `500` bodies additionally carry `"staleClient": true` — the failure is attributed to a client generated against an older model surface rather than to the query itself. A malformed request (`ScryWireException`) is never attributed: it carries no usable stamp. The marker is omitted entirely when the stamps agree or the request sent none.

The `500` message is fixed — `Query execution failed.` — and stack traces, SQL, and EF Core<!-- include: error-500-body. path: /docs/includes/error-500-body.include.md -->
messages are never returned to the client. The only variable part is the `staleClient` marker.<!-- endInclude -->

Log them with the application's normal exception logging.

On the client, a non-success status becomes a `ScryRequestException` carrying `StatusCode` and the raw `Body` — unless the body carries `staleClient`, in which case it becomes a `ScryStaleClientException`, the same type the payload reader throws for an unknown enum value. One catch therefore covers every failure whose remedy is regenerating the client (or reloading the deployed app); see [Schema versioning](schema-versioning.md#detecting-a-stale-client).


## Result payloads

Responses are JSON with camelCased keys and enums as names:

<!-- snippet: ExecutionTests.GroupByWithAggregates.verified.txt -->
<a id='snippet-ExecutionTests.GroupByWithAggregates.verified.txt'></a>
```txt
{
  "version": 1,
  "kind": "List",
  "payload": [
    {
      "region": "North",
      "total": 350.00,
      "count": 2
    },
    {
      "region": "South",
      "total": 75.00,
      "count": 1
    }
  ],
  "stamp": "{scrubbed stamp}"
}
```
<sup><a href='/src/Scry.Tests/ExecutionTests.GroupByWithAggregates.verified.txt#L1-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-ExecutionTests.GroupByWithAggregates.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`kind` is `List`, `Single`, or `Scalar`, matching the terminal that was used.
