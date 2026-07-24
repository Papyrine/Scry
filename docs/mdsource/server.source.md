# Server

`Scry.Server` validates an incoming query AST against the allow-list, rebinds it onto the real EF
Core entity types, applies row policies, executes it against a `DbContext`, and returns the projected
rows.

## Registration

snippet: serverRegistration

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

snippet: mapScry

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

snippet: scryOptionsLimits

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

The sequence is wrapped with `AsQueryable()`, so the pipeline runs in memory over LINQ to Objects
with the same validation, shaping, and limits as a database source.

## Row policies

```cs
options.AddPolicy<Employee, TenantPolicy>();
```

Applies an `IReturnablePolicy<T>` to a source before any client operator, so client filters can only
narrow an already-authorized set. Overrides `[ReturnableWith]` on the same type. See
[Row policies](policies.md).

## Hosting without the HTTP endpoint

`ScryProcessor` is the programmatic entry point that `MapScry` wraps. Use it directly for a different
transport (SignalR, gRPC, a message queue) or in tests. `ScryProcessor.Create<TContext>` takes the
same configuration delegate as `AddScry`:

snippet: processorCreate

Execute a request against any `DbContext` instance:

```cs
var response = processor.Execute(request, dbContext);
```

There are two `Execute` overloads: one taking an `IServiceProvider` (used to resolve policies) and
one without, which falls back to activating policies via their parameterless constructor.

`processor.Describe()` returns the [introspection](explorer.md#introspection) contract.

Because `ScryClient` takes an arbitrary transport delegate, the same processor also makes an
in-process client easy — the whole pipeline, LINQ to rows, with no web host:

snippet: inProcessClient

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

The `500` message is fixed. Internal details — stack traces, SQL, EF messages — are never returned to
the client. Log them with your normal exception logging.

On the client, a non-success status becomes a `ScryRequestException` carrying `StatusCode` and the
raw `Body`.

## Result payloads

Responses are JSON with camelCased keys and enums as names:

snippet: ExecutionTests.GroupByWithAggregates.verified.txt

`kind` is `List`, `Single`, or `Scalar`, matching the terminal that was used.
