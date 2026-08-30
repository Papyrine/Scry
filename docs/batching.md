# Batching

A page load rarely needs one query. A dashboard opens with a list, a count, and a lookup; each is a separate `POST`, and on a WebAssembly client over a real network the round-trips dominate — the queries themselves were never the slow part. A batch sends them together.

Batching is a **transport concern, not a query operator**. Nothing about it reaches the wire request, and the server sees each entry exactly as it would have arrived alone: same validation, same [row policies](policies.md), same [audit trail](observability.md). What changes is only how many requests carry them.


## Writing one

Start a batch, attach it to each query with `InBatch`, then send:

<!-- snippet: clientBatch -->
<a id='snippet-clientBatch'></a>
```cs
var batch = client.Batch();

// Each terminal returns a task that completes when the batch is sent — so collect them first,
// then send, then await. Awaiting one before SendAsync would wait forever.
var employees = client.Source<Employee>("Employee")
    .Where(_ => _.Active)
    .OrderBy(_ => _.Name)
    .Select(_ => new EmployeeRow(_.Name, _.Status))
    .InBatch(batch)
    .ToListAsync();

var orders = client.Source<Order>("Order")
    .InBatch(batch)
    .CountAsync();

await batch.SendAsync();

var rows = await employees;
var count = await orders;
```
<sup><a href='/src/Scry.Tests/BatchTests.cs#L135-L155' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientBatch' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`InBatch<T>(this IQueryable<T> source, ScryBatch batch)` is the whole client surface. Every terminal works unchanged behind it — `ToListAsync`, `CountAsync`, `FirstOrDefaultAsync`, `ToPageAsync`, the aggregates, all of them — because it changes which request carries the query rather than what the query asks. It sits anywhere in the chain, and, like the [header operators](querying.md#headers), is invisible to translation: the wire request is byte-for-byte the one the unbatched query would have sent.

> **The task completes when the batch is sent.** Awaiting a query's task *before* `SendAsync` waits forever — the request has not been sent, so nothing can complete it. Collect the tasks, send, then await. This is the one rule batching adds.

A batch is a client-side collector, used once, from the one thread that builds it. `SendAsync` twice throws, as does attaching a query to a batch already sent.


## Entries are independent

Each entry is validated, policy-filtered, and executed on its own, so one being rejected leaves the rest answered:

```cs
var batch = client.Batch();
var good = Query.Employee.InBatch(batch).CountAsync();
var bad = Query.Employee.InBatch(batch).Where(_ => /* something the server refuses */).ToListAsync();

await batch.SendAsync();

var count = await good;      // answered
var rows = await bad;        // throws, exactly as it would have unbatched
```

A rejected entry faults its own task with the same exception the query would have raised sent alone — `ScryRequestException`, `ScryStaleClientException` when the rejection is attributed to [schema drift](schema-versioning.md), or `ScryPermissionException` where a [row policy denied it](policies.md#what-a-denied-row-produces). Code that already handles a failed query needs no second shape for one that happened to be batched. One entry's rows being denied says nothing about another's, so the rest are answered as usual.

`SendAsync` itself throws only when the **batch** failed: the transport, an unreadable response, or a rejection of the whole envelope. Such a failure also faults every entry's task, so a caller awaiting them is never left waiting on a response that is not coming.


## What a batch is not

- **Not a transaction.** Entries run sequentially against one `DbContext`, in order, with no shared transaction. An entry that fails leaves the entries before it answered.
- **Not parallelism.** A batch saves round-trips, not database time. `DbContext` is not thread-safe and a batch has no reason to work around that — the win being chased is the network, not the server.
- **Not a way around a limit.** Every [per-query limit](server.md#options) applies to every entry, and `MaxBatchSize` bounds how many entries there can be.
- **Not for streaming.** [`ToAsyncEnumerable`](querying.md#streaming-rows) reads a response row by row; a batch is answered as one response. A streamed query inside a batch is refused rather than quietly sent on its own.
- **Not for [per-query headers](querying.md#headers).** One request carries the batch, so its queries have none of their own to write a header onto. Attaching a header to a batched query is refused at the point it is attached; set it on the `HttpClient` instead.


## Limits

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxBatchSize` | 20 | Maximum entries in one batch. Exceeded, the batch is rejected whole — before any entry runs. |

A batch is the one place a single request costs more than one query, which makes it the one place worth bounding separately: every other limit is per query and would otherwise apply to an arbitrary number of them at once. As with the other limits, this bounds the *shape* of a request rather than its cost — see [what Scry does not do](security.md#what-scry-does-not-do).


## Observability

Nothing special is needed. Each entry produces its own [span, metrics, and audit entry](observability.md), so a batch is not a blind spot in the trail — what was asked is what is recorded, and a batch asked more than once. The entries' spans nest under one `scry.batch` span tagged with `scry.batch.size`.


## Transports

Batching is available on any client built by `ScryClient.ForHttp`, which posts to the [`…/batch` endpoint](server.md#mapping-the-endpoint) that `MapScry` maps alongside the others.

A [custom transport](server.md#hosting-without-the-http-endpoint) supplies its own, or has none — in which case `Batch()` says so rather than sending the queries one at a time and calling it a batch:

```cs
var client = new ScryClient(
    (request, cancel) => /* single */,
    batchTransport: (request, cancel) => Task.FromResult(processor.ExecuteBatch(request, db)));
```

`ScryProcessor.ExecuteBatch` is the server-side entry point, and the same choke point `Execute` is — so a host without HTTP gets batching, validation, policies, and telemetry on the same terms.
