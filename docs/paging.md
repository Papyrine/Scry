# Paging

> **Status: Slice 1 implemented; cursors (slices 2–3) pending.** Offset paging — the `ToPageAsync`
> terminal, the `ScryPage` envelope, `HasMore`, and `DefaultPageSize` — is live. The keyset **cursor**
> (opaque token, server-appended total order, seek predicate) is the target design described below but
> is not built yet, so `Cursor` is always null and you advance pages with `Skip`. This page governs
> the full design the way [security.md](security.md) governs the wire.

Scry's paging model is **keyset-native**: the primary, blessed way to page a large result set is an
opaque server-issued **cursor** that resumes a stable ordering. Offset paging (`Skip`/`Take`) remains
available for small ad-hoc jumps, but cursors are what the docs push and what scales.

This mirrors the mature systems — it is OData's `$orderby` + `$top` + `$skiptoken` and Relay's
`first` + cursor + `pageInfo`, unified into Scry's captured-LINQ grammar. The difference is that the
server does the parts a hostile client cannot be trusted with: guaranteeing a **total order** and
turning the cursor back into an allow-listed seek predicate.

## Why keyset, not offset

Offset paging (`OrderBy(...).Skip(n).Take(m)`) already works today, and it is fine for a five-page
admin table. It is the wrong default for a real paging feature for two well-known reasons:

- **It is unstable under concurrent writes.** A row inserted or deleted before the current offset
  shifts every later row, so page *n+1* silently duplicates or skips rows relative to page *n*.
- **It degrades.** `Skip(100_000)` makes the database walk and discard 100 000 rows on every page.

Keyset paging seeks directly to the resume point (`WHERE key > lastKey ORDER BY key`), which is O(1)
in the page offset and stable across writes. Relay made cursors idiomatic for exactly this reason;
OData added `$skiptoken` on top of `$skip` for the same reason. Scry follows suit.

## The grammar — one new terminal

Paging adds a single client terminal, `ToPageAsync`, and otherwise reuses the operators the client
already writes: an `OrderBy` for the sort, a page size, and (once cursors land) a token handed back
from the previous page.

```cs
// Page 1 — an ordered query with a page size.
var page = await Query.Employee
    .Where(_ => _.Active)
    .OrderBy(_ => _.Created)
    .ThenBy(_ => _.Id)
    .ToPageAsync(20);

foreach (var row in page.Items) { /* ... */ }

// Page 2 — TARGET (slices 2–3): the same query, resumed with the previous page's cursor.
var next = await Query.Employee
    .Where(_ => _.Active)
    .OrderBy(_ => _.Created)
    .ThenBy(_ => _.Id)
    .ToPageAsync(20, page.Cursor);

// Page 2 — TODAY (slice 1): advance with Skip, stopping on HasMore.
var next = await Query.Employee
    .Where(_ => _.Active)
    .OrderBy(_ => _.Created)
    .ThenBy(_ => _.Id)
    .Skip(20)
    .ToPageAsync(20);
```

Roles of the pieces:

| Piece | Role |
| --- | --- |
| `ToPageAsync(n)` | The paging terminal. `n` is the **page size** (a parameter, not a trailing `Take`, so it is unambiguous next to any `Skip`). Capped by `MaxPageSize`, defaulted by `DefaultPageSize` when omitted (`ToPageAsync()`). Returns `ScryPage<T>` of `Items` + `HasMore` + `Cursor`. |
| `OrderBy` / `ThenBy` | The **sort**. Required for a cursor — with no order there is no stable resume point. |
| `Skip(n)` | Advances offset-style in slice 1. Superseded by the cursor once keyset paging lands. |
| `Cursor` | *(Slices 2–3)* opaque resume token from the previous page; null today. |

## What the server guarantees

The client's ordering is not enough on its own — `OrderBy(_ => _.Created)` is not unique, so two rows
with the same `Created` have no defined relative order and a cursor could straddle them. The server
closes this:

1. **Total order.** *(Slices 2–3)* the server appends the source's **primary key** (which it knows
   from EF metadata) as a final tiebreaker to the client's ordering, unless the ordering already ends
   in a unique key. This is invisible to the projection — the key is used for ordering and the cursor
   only, never surfaced in the result unless the client projected it.
2. **`HasMore` without a count.** *(Slice 1 — implemented.)* The server fetches `n + 1` rows, returns
   `n`, and sets `HasMore` from whether the extra row existed. No `COUNT(*)` is issued.
3. **The cursor.** *(Slices 2–3)* the server encodes the ordering-key tuple of the last returned row
   into an opaque token (see [Cursor format](#cursor-format)).
4. **Resume.** *(Slices 2–3)* on the next call the server decodes the cursor and adds a
   **lexicographic seek predicate** over the ordering keys, then re-runs the identical pipeline.

For an ascending `a`, descending `b`, tiebreak `pk`, the seek predicate is:

```
WHERE  a > a0
   OR (a = a0 AND b < b0)
   OR (a = a0 AND b = b0 AND pk > pk0)
```

This is ordinary SQL that EF Core translates; it introduces no new execution concept, only additional
`WhereOp`-equivalent comparisons built server-side.

## Total count is separate

Neither the page nor the cursor carries a total row count. A count is a second, potentially expensive
query, so — as with OData's opt-in `$count` and Relay's separate `totalCount` — it stays out of the
page. Scry already exposes it as its own terminal; ask for it explicitly when you need it:

```cs
var total = await Query.Employee.Where(_ => _.Active).CountAsync();
```

## Limits

Paging is governed by two `ScryOptions` settings. `MaxPageSize` already exists as the ceiling on an
explicit `Take`; `DefaultPageSize` is added so an unbounded paged query is bounded rather than
returning the whole table.

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxPageSize` | 1000 | Hard ceiling on the page size. A `Take`, or a `ToPageAsync` size, above it is rejected at validation. |
| `DefaultPageSize` | 100 | Page size applied to a `ToPageAsync()` that omits a size. |

`DefaultPageSize` closes the paging hole where an unbounded page would otherwise bypass `MaxPageSize`:
a paged result is always a bounded page, and `HasMore` tells the caller whether more exists.
Truncation is never silent. (`ToListAsync` is unchanged — it still returns the full authorized set,
bounded only by an explicit `Take` and by row policies; "give me everything" stays an explicit choice.)

> **Note.** `MaxPageSize` bounds the *shape* of one page, not the total cost of walking a large table
> across many pages. It is not a rate limiter. Cost control still belongs to
> [row policies](policies.md), ASP.NET Core rate limiting, and a command timeout — see
> [security.md](security.md#what-scry-does-not-do).

## Wire format

Paging is **additive** — the wire version stays at its current value, older clients and the existing
terminals are untouched. It adds a terminal operator and a result kind against
[wire-format.md](wire-format.md):

### Request — a `page` terminal

Paging adds one terminal operator, `page`, carrying the requested page `size` (omitted for the server
default). *(Slices 2–3)* it also gains an opaque `cursor` — the resume token from the previous page's
response; a client must treat it as a bytestring and never parse or synthesize one.

```json
{
  "version": 1,
  "root": "Employee",
  "pipeline": [
    { "$type": "orderBy", "key": { "$type": "member", "path": ["Created"] }, "descending": false },
    { "$type": "page", "size": 20 }
  ]
}
```

### Response — a page envelope (`Page` kind)

A `page` terminal returns the new `Page` result kind, whose payload is a `ScryPage` envelope rather
than a bare row array. `List` (from `ToListAsync`) is unchanged.

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
| `cursor` | *(Slices 2–3)* opaque resume token for the next page; omitted when null (as it always is today). |

### Cursor format

*(Slices 2–3.)* The cursor is internal to the server and its shape is **not** part of the wire
contract — clients must not depend on it. The reference encoding is a version-tagged, base64url-encoded
JSON tuple of the ordering-key values of the last row:

```
v1.<base64url(json)>   where json = { "k": [ <orderKey0>, <orderKey1>, ..., <pk> ] }
```

Values are carried in the same invariant-culture string + `ClrTypeTag` form the wire uses for
constants, so decoding a cursor produces exactly the `ConstNode`s the seek predicate needs.

## Security

A cursor introduces **no new attack surface**. When decoded it becomes `Where`-style comparisons over
**allow-listed** ordering members, which flow through the same `QueryValidator` and
`IReturnablePolicy<T>` as any other predicate. The worst a tampered cursor can do is seek to an
arbitrary point *within an already-authorized, already-policy-filtered set* — it cannot widen the set,
reach an unlisted member, or bypass a row policy.

The reference cursor is nonetheless **HMAC-signed** with a server key. This is not a confidentiality
or authorization control — it enforces the "opaque, do not parse" contract and rejects malformed
tokens early with a clear `400` rather than letting them fall through to a seek over garbage. The
signing key is server-only and never leaves the server.

## Scope

The `page` terminal targets a **non-grouped** query. Out of scope:

- **Grouped / aggregated results.** A `page` over a `GroupBy` → `Select` pipeline is **rejected** at
  validation (`Paging is not supported over a grouped query`); grouped results use `Skip`/`Take`.
- **`Single` / `First` terminals.** These already bound their result and take no page.
- **Unordered queries.** Slice 1 allows `ToPageAsync` without an `OrderBy` (plain offset with no
  cursor). An `OrderBy` becomes *required* when cursors land, since a cursor needs a stable order.

## Delivery plan

The design is delivered in slices so the fiddly seek-predicate generation does not block the useful
first increment:

1. **Envelope + `DefaultPageSize` — ✅ done.** The `page` terminal, `ScryPage` envelope, `HasMore` via
   the `n + 1` fetch, and `DefaultPageSize`, advancing with `Skip`. Complete, safe offset paging;
   makes `MaxPageSize` coherent. Cursor is null.
2. **Single-key cursor.** Append the primary key, issue and decode a two-term seek (`ORDER BY key` +
   tiebreak). Covers the common "order by one column" case. Adds `cursor` to the `page` op and terminal.
3. **Multi-key cursor.** Full lexicographic seek over an arbitrary `OrderBy`/`ThenBy` chain, including
   mixed directions and nullable keys.

## Open questions

- **Unordered paged query when cursors land.** Slice 1 permits `ToPageAsync` with no `OrderBy` (offset
  only). Once cursors exist, do we reject the unordered case outright, or keep allowing it as
  cursor-less offset paging? Rejecting is more honest; allowing is more forgiving.
- **Cursor invalidation.** A cursor encodes an ordering; if the caller changes the `OrderBy` between
  pages the seek is meaningless. Options: bind the cursor to a hash of the pipeline and reject a
  mismatch, or document it as caller error.
