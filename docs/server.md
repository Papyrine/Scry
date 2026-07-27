# Server

`Scry.Server` validates an incoming query AST against the allow-list, rebinds it onto the real EF
Core entity types, applies row policies, executes it against a `DbContext`, and returns the projected
rows.

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

`AddPocoSource` registers the data for a `[QueryablePoco]` type — see
[POCO sources](#poco-sources) below. `MaxPageSize` is one of the [limits](#options).

`AddScry<TContext>`:

- Scans `typeof(TContext).Assembly` for types carrying `[Queryable]`, `[QueryableView]`, or
  `[QueryablePoco]`.
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
<sup><a href='/samples/Sample.Server/Program.cs#L43-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

One `POST` endpoint. The request body is a [`QueryRequest`](wire-format.md); the response body is a
`QueryResponse`. `MapScry` returns an `IEndpointConventionBuilder`, so the usual conventions apply:

```cs
app.MapScry("/api/query")
    .RequireAuthorization()
    .RequireCors("client");
```

Authentication and authorization are **not** Scry's job — put them on the endpoint. See
[Security model](security.md).

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
```
<sup><a href='/src/Scry.Server/ScryOptions.cs#L9-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-scryOptionsLimits' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Every limit is enforced during validation, before any expression is rebound or executed.

| Option | Default | Rejects |
| --- | --- | --- |
| `MaxPageSize` | 1000 | `Take n` where `n` exceeds it. Note this caps an explicit `Take`; it does not implicitly page an unbounded query. |
| `MaxNavigationDepth` | 4 | Member paths longer than the limit, and projection nesting deeper than it. |
| `MaxPipelineLength` | 32 | Pipelines with more operators than the limit. |
| `MaxExpressionDepth` | 32 | Predicate/expression trees nested deeper than the limit. |

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

`ScryProcessor` is the programmatic entry point that `MapScry` wraps. Use it directly for a different
transport (SignalR, gRPC, a message queue) or in tests. `ScryProcessor.Create<TContext>` takes the
same configuration delegate as `AddScry`:

<!-- snippet: processorCreate -->
<a id='snippet-processorCreate'></a>
```cs
static ScryProcessor Processor(Action<ScryOptions>? extra = null) =>
    ScryProcessor.Create<TestContext>(options =>
    {
        options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        extra?.Invoke(options);
    });
```
<sup><a href='/src/Scry.Tests/ExecutionTests.cs#L239-L246' title='Snippet source file'>snippet source</a> | <a href='#snippet-processorCreate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Execute a request against any `DbContext` instance:

```cs
var response = processor.Execute(request, dbContext);
```

There are two `Execute` overloads: one taking an `IServiceProvider` (used to resolve policies) and
one without, which falls back to activating policies via their parameterless constructor.

`processor.Describe()` returns the [introspection](explorer.md#introspection) contract.

Because `ScryClient` takes an arbitrary transport delegate, the same processor also makes an
in-process client easy — the whole pipeline, LINQ to rows, with no web host:

<!-- snippet: inProcessClient -->
<a id='snippet-inProcessClient'></a>
```cs
static ScryClient ClientFor(TestContext context)
{
    var processor = ScryProcessor.Create<TestContext>(
        _ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

    return new((request, _) => Task.FromResult(processor.Execute(request, context)));
}
```
<sup><a href='/src/Scry.Tests/ClientRoundTripTests.cs#L284-L292' title='Snippet source file'>snippet source</a> | <a href='#snippet-inProcessClient' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Execution pipeline

For each request the server, in order:

1. **Validates** the whole AST against the schema and the limits. Any violation throws
   `ScryValidationException` and nothing else runs.
2. **Resolves** the source — `DbContext.Set<T>()` for entities and views, the registered factory for
   POCOs.
3. **Applies the row policy**, if the source has one.
4. **Rebinds** each operator onto real CLR expression trees over the entity type. CLR types come only
   from the schema, never from the wire.
5. **Executes** through the underlying provider — EF Core translates to SQL for entities and views.
6. **Shapes** the result: a `Select` to `object[]` of the requested leaves, folded back into
   (possibly nested) JSON objects using the projection plan.

Because the projection is applied inside the query, only the requested columns are read from the
database.

Terminal handling:

- `Count` / `Any` execute as scalars.
- A predicate on `First` / `Single` is applied **before** the projection.
- `First` / `Single` return a single object, or `null` for the `OrDefault` variants over an empty
  sequence.

## Error handling

The endpoint maps failures deliberately:

| Cause | Status | Body |
| --- | --- | --- |
| Malformed JSON, unknown discriminator, wrong shape (`ScryWireException`) | `400` | `{"error":"..."}` |
| Allow-list or limit violation (`ScryValidationException`) | `400` | `{"error":"..."}` |
| Anything else | `500` | `{"error":"Query execution failed."}` |

The `500` body is fixed — `{"error":"Query execution failed."}` — and stack traces, SQL, and EF Core<!-- include: error-500-body. path: /docs/includes/error-500-body.include.md -->
messages are never returned to the client.<!-- endInclude -->

Log them with your normal exception logging.

On the client, a non-success status becomes a `ScryRequestException` carrying `StatusCode` and the
raw `Body`.

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
  ]
}
```
<sup><a href='/src/Scry.Tests/ExecutionTests.GroupByWithAggregates.verified.txt#L1-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-ExecutionTests.GroupByWithAggregates.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`kind` is `List`, `Single`, or `Scalar`, matching the terminal that was used.
