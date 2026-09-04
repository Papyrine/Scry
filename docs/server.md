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
        // Department.Handbook and Employee.Photo are [Attachment]s, and one exposed without a
        // check is a startup failure. Registered here rather than by [AttachmentWith] because
        // the model project references the annotations alone and has no server type to name.
        _.AddAttachmentPolicy<Department, HandbookPolicy>();
        _.AddAttachmentPolicy<Employee, PhotoPolicy>();
        _.MaxPageSize = 200;

        // A row policy whose decision is too slow to run per row in SQL, so it runs in C# and
        // the server remembers what it answered. Revision is what tells it a row has changed
        // and needs deciding again — see /docs/policies.md and the /permissions page.
        _.AddCachedPolicy<Order, long, RegionAccessPolicy>(_ => _.Revision);

        // Repeat a query while nothing has been written and the answer is a 304 rather than a
        // re-execution. Optional, and off until a freshness source says how to tell — see
        // /docs/caching.md.
        _.UseDeltaFreshness<SampleContext>();

        // What a cached response belongs to. This server has sources whose answers depend on
        // who asked — the row policy above, and Department.Handbook's attachment check — and
        // MapScry refuses to start without this. The sample has no sign-in, so the caller
        // half is a constant; a real app returns its tenant or its principal, and a client
        // signing in as someone else is then never handed the previous one's rows.
        //
        // The grants version is the other half, and is the part worth copying. A response
        // varies by what the caller is allowed to see, and QueryFreshness only watches the
        // database — so a grant changing outside it would move nothing, and a cache holding
        // the old rows would go on answering with rows the caller has since lost.
        _.CacheScope = _ => $"sample-{_.RequestServices.GetRequiredService<RegionGrants>().Version}";
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L31-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-serverRegistration' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='/samples/Sample.Server/Program.cs#L85-L87' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Three routes, from the one call:

| Route | Method | Request | Response |
| --- | --- | --- | --- |
| the pattern given | `GET` or `POST` | [`QueryRequest`](wire-format.md), [in the URL](wire-format.md#the-url-form) or in the body | one `QueryResponse` |
| `…/stream` | `POST` | the same `QueryRequest` | [newline-delimited rows](wire-format.md#streamed-results), for [`ToAsyncEnumerable`](querying.md#streaming-rows) |
| `…/batch` | `POST` | [`QueryBatchRequest`](wire-format.md#batched-queries) | one result per entry, for [batching](batching.md) |

(Plus `…/attachment`, which [attachments](attachments.md) covers — mapped here so one authorization convention reaches it too.)

The query pattern answers both methods with the same handler: a `GET` carries the request base64url-encoded in a `q` parameter instead of in a body, and everything after that — validation, the allow-list, policies, shaping — is identical. `GET` is what a client uses by default, because a response identified by a URL can be cached and revalidated by machinery that already exists where a `POST` can never be; see [Caching](caching.md).

Setting [`QueryUrlLimit`](#options) to `0` maps no `GET` route at all, and routing then answers one with a `405` naming `POST`. Clients notice and stop offering URLs, so the status is a backstop for a stale one rather than the everyday path.

The routes are mapped together deliberately. Streaming reads the same query surface a row at a time and batching carries several of its queries at once; neither widens what can be asked, so opting into them separately would only invite deployments where one is protected and the others are not. `MapScry` returns an `IEndpointConventionBuilder` covering **every** route it mapped, so the usual conventions apply once:

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

/// <summary>
/// Maximum number of operators in a query pipeline. The pipeline a join's inner side or a set
/// operand carries is bounded by the same number. Default 32.
/// </summary>
public int MaxPipelineLength { get; set; } = 32;

/// <summary>Maximum expression nesting depth in a predicate. Default 32.</summary>
public int MaxExpressionDepth { get; set; } = 32;

/// <summary>
/// Maximum number of members a projection may name, nested members included, and the same for
/// the members a join projects. Default 256.
/// </summary>
/// <remarks>
/// Every member is an expression the provider compiles and a column the query returns, so the
/// width of a projection is work a request asks for, exactly as the length of its pipeline is. A
/// query writing no <c>Select</c> is unaffected: its projection is the model's own members.
/// </remarks>
public int MaxProjectionMembers { get; set; } = 256;

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
/// caps <c>Take</c> and a page, not an unbounded enumeration. Nor is streaming the safer of the two
/// server-side any longer — a list that outgrows <see cref="ResponseSpillThreshold"/> is written out
/// as it is read, so neither holds its rows. What both hold is a connection and a response open for
/// as long as the client reads, which is the reason to offer a bound at all. A stream that
/// reaches the limit ends with an error marker rather than a short result, so a client cannot
/// mistake truncation for the end of the data.
/// </remarks>
public int? MaxStreamRows { get; set; }

/// <summary>
/// The longest encoded query this deployment wants asked as a URL. Default 4096; zero maps no GET
/// route at all, so every query travels as a body.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the limits above this one rejects nothing — it is advertised rather than enforced,
/// because the ceiling it describes is not this server's. What actually truncates or refuses a long
/// URL is whichever hop is strictest: 8 KB on a whole request line is the common default for a
/// server or a proxy, and the number here is the budget a client is asked to stay inside of so it
/// never finds out where the real edge is. A request that arrives is answered whatever its length.
/// </para>
/// <para>
/// It is a deployment setting rather than something the model declares, since the ingress in front
/// of a server is a property of where it runs — one model can be hosted behind two of them.
/// Clients learn it from <see cref="WireFormat.UrlLimitHeader"/>, carried on every response.
/// </para>
/// <para>
/// Zero is the exception, and is enforced: it says a query may never appear in a URL here, which is
/// a statement about this deployment rather than a guess about a length. <c>MapScry</c> honours it
/// by not mapping the GET route, so routing answers such a request with a 405 naming POST and Scry
/// never sees it. Setting it means giving up conditional requests — see /docs/caching.md.
/// </para>
/// </remarks>
public int QueryUrlLimit { get; set; } = QueryUrl.MaxLength;
```
<sup><a href='/src/Scry.Server/ScryOptions.cs#L9-L97' title='Snippet source file'>snippet source</a> | <a href='#snippet-scryOptionsLimits' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Every limit is enforced during validation, before any expression is rebound or executed.

| Option | Default | Rejects |
| --- | --- | --- |
| `MaxPageSize` | 1000 | `Take n` where `n` exceeds it. Note this caps an explicit `Take`; it does not implicitly page an unbounded query. |
| `MaxNavigationDepth` | 4 | Member paths longer than the limit, and projection nesting deeper than it. |
| `MaxPipelineLength` | 32 | Pipelines with more operators than the limit, including the pipeline a [join](querying.md#joins)'s inner side or a set operand carries. |
| `MaxExpressionDepth` | 32 | Predicate/expression trees nested deeper than the limit. |
| `MaxProjectionMembers` | 256 | A `Select` naming more members than the limit, nested members counted, and a join projecting more. A query writing no `Select` is unaffected. |
| `MaxInValues` | 1000 | `Contains` over a client-supplied set larger than the limit. Bounds the SQL `IN` list a single request can build. |
| `MaxBatchSize` | 20 | A [batch](batching.md) carrying more queries than the limit — rejected whole, before any entry runs. Every other limit is per query, so this is what stops one request from costing an arbitrary number of them. |
| `MaxStreamRows` | unset | Nothing by default. Set it to cap a [streamed](querying.md#streaming-rows) result; the stream then ends with an error marker rather than a short one. Like `MaxPageSize`, it does not implicitly bound an unbounded query — it bounds one that asked to stream. |

`MaxCachedPolicyKeys` sits beside them but rejects nothing a client asked for. It bounds how many rows one caller may be allowed by a [cached row policy](policies.md#when-the-decision-is-too-expensive-for-sql) before a query is refused rather than run: every allowed key travels to the database with each query, so an allow-list that quietly grew unbounded surfaces as a message rather than as a slow query. Unset by default.

`CaseSensitiveCollation` and `CaseInsensitiveCollation` are not limits but capabilities: both default to null, which rejects a request asking for that case sensitivity. Set them to collations the database has (`Latin1_General_CS_AS`, `Latin1_General_CI_AS` on SQL Server) to enable [case-sensitive matching](querying.md#operators-1). They are server settings because a collation is emitted into the SQL text rather than parameterized, so accepting one from a request would be the only place an attacker-supplied string reached SQL as anything but a parameter.

Treat them like a connection string: **trusted configuration, never a value taken from a request** or from anywhere a caller can influence. A request cannot carry a collation — the wire names a case sensitivity and the string is looked up here — so the only remaining path is a deployment wiring the option up from somewhere it does not control. Both are checked at startup and must be plain names (letters, digits, underscores); a provider does quote the name as well, but that is provider-overridable behaviour rather than a guarantee.

Applying a collation also costs an index. `WHERE col COLLATE X = @p` is not SARGable, so an index built under the column's own collation cannot be seeked and the query degrades to a scan. Where a whole column should be matched one way, setting the **column's** collation is both faster and simpler than asking per query.


### Response size

`ResponseSpillThreshold` is not one of the limits above: crossing it rejects nothing and bounds nothing a client may ask for. It is the size in bytes past which a response stops being held whole.

| Option | Default | Does |
| --- | --- | --- |
| `ResponseSpillThreshold` | 65536 (64 KB) | Under it, a response is sent as one body declaring a `Content-Length`. Over it, the response is sent as it is written, so what is resident is bounded by the threshold rather than by the result. Zero holds every response whole, as every response once was. |

A result that fits behaves exactly as every result did before the threshold existed — nothing reaches the wire until the whole envelope exists, so a failure part-way through reading the rows is still answered as a `400` or a `500` with a body. Past it the status is long since committed and a failure can only truncate the response, which a reader can always tell from a complete one; see [Wire format](wire-format.md#response) for why.

A result carrying [binary transfer](wire-format.md#binary-transfer) values is held whole whatever this says, since its raw parts have to precede the JSON that references them. For a single query that is the projection plan's decision, taken before the first row is read. A batch commits to one framing before its first entry runs, so it asks the coarser question instead: any `[BinaryTransfer]` member anywhere in the model holds every batch whole.

What the threshold buys is that an unbounded `ToListAsync` is no longer resident twice over, as rows and as bytes. What it costs is that past it a response holds a connection and its database read open for as long as the client reads — which is the exposure `…/stream` has always had, and the reason `MaxStreamRows` exists.


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
<sup><a href='/src/Scry.Tests/ClientRoundTripTests.cs#L427-L430' title='Snippet source file'>snippet source</a> | <a href='#snippet-inProcessClient' title='Start of snippet'>anchor</a></sup>
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
  "version": 2,
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
