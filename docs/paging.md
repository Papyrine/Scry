# Paging

Scry's paging model is **keyset-native**: the primary, blessed way to page a large result set is an opaque server-issued **cursor** that resumes a stable ordering. Offset paging (`Skip`/`Take`) remains available for small ad-hoc jumps, but cursors are what the docs push and what scales.

This mirrors the mature systems — it is OData's `$orderby` + `$top` + `$skiptoken` and Relay's `first` + cursor + `pageInfo`, unified into Scry's captured-LINQ grammar. The difference is that the server does the parts a hostile client cannot be trusted with: guaranteeing a **total order** and turning the cursor back into an allow-listed seek predicate.


## Why keyset, not offset

Offset paging (`OrderBy(...).Skip(n).Take(m)`) already works, and it is fine for a five-page admin table. It is the wrong default for a real paging feature for two well-known reasons:

- **It is unstable under concurrent writes.** A row inserted or deleted before the current offset shifts every later row, so page *n+1* silently duplicates or skips rows relative to page *n*.
- **It degrades.** `Skip(100_000)` makes the database walk and discard 100 000 rows on every page.

Keyset paging seeks directly to the resume point (`WHERE key > lastKey ORDER BY key`), which is O(1) in the page offset and stable across writes — Markus Winand's [use-the-index-luke.com/no-offset](https://use-the-index-luke.com/no-offset) is the canonical write-up. [Relay](https://relay.dev/graphql/connections.htm) made cursors idiomatic for exactly this reason; [OData](https://learn.microsoft.com/en-us/odata/webapi/skiptoken-for-server-side-paging) added `$skiptoken` on top of `$skip` for the same reason. Scry follows suit.


## The grammar

Paging is expressed through a single client terminal, `ToPageAsync`; everything else reuses the operators the client already writes: an `OrderBy` for the sort, a page size, and a cursor handed back from the previous page.

<!-- snippet: pagingGrammar -->
<a id='snippet-pagingGrammar'></a>
```cs
// Page 1 — an ordered query with a page size.
var page = await Query.Employee
    .Where(_ => _.Active)
    .OrderBy(_ => _.Created)
    .ThenBy(_ => _.Id)
    .ToPageAsync(20);

foreach (var row in page.Items)
{
     /* ... */
}

// Page 2 — the same query, resumed with the previous page's cursor (a keyset seek).
if (page.HasMore)
{
    var next = await Query.Employee
        .Where(_ => _.Active)
        .OrderBy(_ => _.Created)
        .ThenBy(_ => _.Id)
        .ToPageAsync(20, page.Cursor);
}
```
<sup><a href='/samples/Sample.Client/Pages/PagingGrammar.cs#L10-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-pagingGrammar' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Roles of the pieces:

| Piece | Role |
| --- | --- |
| `ToPageAsync(n[, cursor])` | The paging terminal. `n` is the **page size** (a parameter, not a trailing `Take`, so it is unambiguous next to any `Skip`). Capped by `MaxPageSize`, defaulted by `DefaultPageSize` when omitted. `cursor` resumes a previous page. Returns `ScryPage<T>` of `Items` + `HasMore` + `Cursor`. |
| `OrderBy` / `ThenBy` | The **sort**. Required for a cursor — with no order there is no stable resume point. |
| `Skip(n)` | Offset paging, an alternative to the cursor for small ad-hoc jumps. A `Skip` present makes the page offset-mode (no cursor emitted). |
| `Cursor` | Opaque resume token from the previous page; null on the last page and for a non-[seek-safe](#the-seek-safe-rule) query. |


## What the server guarantees

The client's ordering is not enough on its own — `OrderBy(_ => _.Created)` is not unique, so two rows with the same `Created` have no defined relative order and a cursor could straddle them. The server closes this:

1. **Total order.** The server appends the source's **primary key** (which it knows from EF metadata) as a final ascending tiebreaker to the client's ordering, unless the ordering already ends in it. This is invisible to the projection — the key is used for ordering and the cursor only, never surfaced in the result unless the client projected it.
2. **`HasMore` without a count.** The server fetches `n + 1` rows, returns `n`, and sets `HasMore` from whether the extra row existed. No `COUNT(*)` is issued.
3. **The cursor.** The server encodes the ordering-key tuple (including the appended primary key) of the last returned row into an opaque, signed token (see [Cursor format](#cursor-format)).
4. **Resume.** On the next call the server decodes the cursor and adds a **lexicographic seek predicate** over the ordering keys, then re-runs the identical pipeline.

For an ascending `a`, descending `b`, tiebreak `pk`, the seek predicate is:

```
WHERE  a > a0
   OR (a = a0 AND b < b0)
   OR (a = a0 AND b = b0 AND pk > pk0)
```

This is ordinary SQL that EF Core translates; it introduces no new execution concept, only additional `WhereOp`-equivalent comparisons built server-side.


## The seek-safe rule

A correct keyset seek needs a **total order over non-null, comparable keys**. The server emits (and accepts) a cursor only when the query is **seek-safe**:

1. the source is an **entity with a primary key** (a view or POCO has none),
2. the client supplied **≥ 1 `OrderBy`** and it is the **trailing** restricting op — no `Where`, `Skip`, or `Take` after it (a `Skip`/`Take` anywhere means offset intent), and
3. every client ordering key is a **single-segment scalar member** that EF reports **non-nullable**.

When a query is not seek-safe the response falls back to offset paging: `Cursor` is null and the caller advances with `Skip`. Passing a cursor to a non-seek-safe query is rejected with a `400`. This deliberately sidesteps NULL-ordering and nav-path seek correctness (where a naive `>`/`<` seek would skip or duplicate rows) while covering the common case — filter, then sort by stable columns.


## Total count is separate

Neither the page nor the cursor carries a total row count. A count is a second, potentially expensive query, so — as with OData's opt-in `$count` and Relay's separate `totalCount` — it stays out of the page. Scry already exposes it as its own terminal; ask for it explicitly when needed:

```cs
var total = await Query.Employee.Where(_ => _.Active).CountAsync();
```


## Limits

Paging is governed by these `ScryOptions` settings:

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxPageSize` | 1000 | Hard ceiling on the page size. A `Take`, or a `ToPageAsync` size, above it is rejected at validation. |
| `DefaultPageSize` | 100 | Page size applied to a `ToPageAsync()` that omits a size. |
| `CursorSigningKey` | *(ephemeral)* | HMAC key for cursor signatures. When unset, a random per-process key is used — cursors then do not survive a restart or work across instances. Set a stable key for a scaled-out or restart-tolerant deployment. |

`DefaultPageSize` closes the paging hole where an unbounded page would otherwise bypass `MaxPageSize`: a paged result is always a bounded page, and `HasMore` tells the caller whether more exists. Truncation is never silent. (`ToListAsync` is unchanged — it still returns the full authorized set, bounded only by an explicit `Take` and by row policies; "give me everything" stays an explicit choice.)

> **Note.** `MaxPageSize` bounds the *shape* of one page, not the total cost of walking a large table across many pages. It is not a rate limiter. Cost control still belongs to [row policies](policies.md), ASP.NET Core rate limiting, and a command timeout — see [security.md](security.md#what-scry-does-not-do).


## Wire format

Paging is **additive** on the wire — the wire version is unchanged, and older clients and the existing terminals are unaffected. It defines one terminal operator and one result kind on top of [wire-format.md](wire-format.md):


### Request — a `page` terminal

The `page` terminal carries the requested page `size` (omitted for the server default) and an optional opaque `cursor` — the resume token from a previous page's response, which a client must treat as a bytestring and never parse or synthesize.

```json
{
  "version": 1,
  "root": "Employee",
  "pipeline": [
    { "$type": "orderBy", "key": { "$type": "member", "path": ["Created"] }, "descending": false },
    { "$type": "page", "size": 20, "cursor": "eyJrZXlzIjpb...w9.Ab3f..." }
  ]
}
```


### Response — a page envelope (`Page` kind)

A `page` terminal returns the `Page` result kind, whose payload is a `ScryPage` envelope rather than a bare row array. `List` (from `ToListAsync`) is unchanged.

```json
{
  "version": 1,
  "kind": "Page",
  "payload": {
    "items": [ { "name": "Alice", "created": "2026-01-04" } ],
    "hasMore": true
  }
}
```

| Field | Meaning |
| --- | --- |
| `items` | The projected rows for this page. |
| `hasMore` | Whether a further page exists. |
| `cursor` | Opaque resume token for the next page; omitted on the last page and for a non-seek-safe query. |


### Cursor format

The cursor's shape is internal to the server and **not** part of the wire contract — clients must not depend on it. The encoding (`CursorCodec`) is `base64url(json) "." base64url(hmac)`, where the JSON is the tagged ordering-key values of the last row:

```
{ "keys": [ { "value": "Alice", "tag": "String" }, { "value": "42", "tag": "Int32" } ] }
```

Values use the same invariant-culture string + `ClrTypeTag` form the wire uses for constants, so decoding a cursor produces exactly the `ConstNode`s the seek predicate rebinds against each key's real type. The HMAC (`HMACSHA256` over the JSON, keyed by `CursorSigningKey`) is checked in constant time on decode; a bad signature or malformed token is a `400`.


## Security

A cursor introduces **no new attack surface**. When decoded it becomes `Where`-style comparisons over **allow-listed** ordering members, which flow through the same `QueryValidator` and `IReturnablePolicy<T>` as any other predicate. The worst a tampered cursor can do is seek to an arbitrary point *within an already-authorized, already-policy-filtered set* — it cannot widen the set, reach an unlisted member, or bypass a row policy.

The cursor is nonetheless **HMAC-signed** (`CursorSigningKey`, or a per-process ephemeral key). This is not a confidentiality or authorization control — it enforces the "opaque, do not parse" contract and rejects malformed tokens early with a clear `400` rather than letting them fall through to a seek over garbage. The signing key is server-only and never leaves the server.


### Is signing necessary? (belt-and-suspenders, by design)

Opaque cursors are universal ([Relay/GraphQL connections](https://relay.dev/graphql/connections.htm), [OData `$skiptoken`](https://learn.microsoft.com/en-us/odata/webapi/skiptoken-for-server-side-paging), [Stripe](https://docs.stripe.com/api/pagination), GitHub, AWS `NextToken`, DynamoDB `LastEvaluatedKey`). **Signing** them is not: API designs split into two camps.

- **Unsigned, re-validated** — [Stripe](https://docs.stripe.com/api/pagination) (`starting_after` is an object ID) and most [Relay](https://relay.dev/graphql/connections.htm) implementations (cursors are plain `base64(...)`). Tampering is harmless because the value is re-scoped and re-authorized server-side on every request.
- **Signed or encrypted** — much of [AWS](https://github.com/amazon-archives/realworld-serverless-application/wiki/List-API-Pagination) and JWT-style stateless tokens, so the server can reject anything it did not mint and treat the payload as trusted.

Scry belongs to the **first** camp: a decoded cursor is re-validated and policy-filtered like any predicate (see above), so tampering is already safe. The HMAC is therefore **optional hardening**, not load-bearing — it only buys fail-fast rejection and the opaque contract. Dropping it and shipping a plain `base64` payload would be a defensible, Relay-style choice.

Two caveats if it is kept:

- **HMAC signs, it does not hide.** The payload is readable — anyone can base64-decode the cursor and see the ordering-key values (`Name = "Alice"`, `Id = 1`). For Scry that is low-sensitivity: the client ordered by those columns and already saw those rows. But if a cursor could ever carry something a client should not read, use authenticated **encryption** (e.g. AES-GCM), not bare HMAC — which is why [AWS recommends encrypting](https://github.com/amazon-archives/realworld-serverless-application/wiki/List-API-Pagination) its pagination tokens.
- **A signed self-contained token is stateful about its key.** With the ephemeral default key a cursor dies on restart or across instances; set a stable `CursorSigningKey` for a scaled-out or restart-tolerant deployment (see [Limits](#limits)).


### Handling a rejected cursor

An `Invalid paging cursor.` `400` is not always the client's fault. Beyond a genuinely malformed token, it is what a **valid** cursor produces once the key that signed it is gone — a restart or a different instance under the ephemeral default. Treat it as *resume point lost*, and re-request the first page rather than surfacing an error: the rows are all still there.

If the same redeploy also changed the model, the rejection additionally carries the [stale-client marker](schema-versioning.md#when-the-break-arrives) and reaches the client as `ScryStaleClientException`, so an app already handling that gets the reload prompt instead — no paging-specific work needed. The plain-`400` case is the one worth a retry-from-the-start path.


## Scope

The `page` terminal targets a **non-grouped** query. Out of scope:

- **Grouped / aggregated results.** A `page` over a `GroupBy` → `Select` pipeline is **rejected** at validation (`Paging is not supported over a grouped query`); grouped results use `Skip`/`Take`.
- **`Single` / `First` terminals.** These already bound their result and take no page.
- **Nullable / nav-path / unordered keys.** Not seek-safe — served by offset with no cursor (see [the seek-safe rule](#the-seek-safe-rule)). True keyset over nullable columns (NULL-ordering aware) is the remaining hard corner and is not currently supported.


## Cursor invalidation

A cursor encodes a specific ordering; changing the `OrderBy` between pages makes the seek meaningless. A cursor whose length does not match the query's ordering is rejected with a `400`, which catches gross mismatches. Subtler mismatches — the same key count over different columns — are not detected; binding the cursor to a full hash of the pipeline would catch them, at the cost of forcing an identical query between pages.
