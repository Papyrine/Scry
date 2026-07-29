# LINQ coverage

What Scry supports compared to the LINQ surface EF Core can translate server-side. This page is both the reference ("will this query work?") and the roadmap — unchecked items are candidates, not commitments.

Scry's wire vocabulary is a **deliberately closed set**. Every operator, function, and expression node must be individually representable, validatable, and rebindable — see the [security model](security.md). So the goal is not parity with EF Core; it is covering the operations remote clients actually need, one auditable addition at a time. Anything outside the set throws `NotSupportedException` on the client at translation time, before a request is sent.

For usage detail on the supported surface (position rules, limits, examples), see [Writing queries](querying.md).


## Supported

### Query operators

| LINQ | Notes |
| --- | --- |
| `Where(predicate)` | |
| `OrderBy(key)` / `OrderByDescending(key)` | Scalar member key. |
| `ThenBy(key)` / `ThenByDescending(key)` | |
| `Skip(n)` / `Take(n)` | `Take` capped by `MaxPageSize`. |
| `Select(projection)` | At most one; must construct an object. |
| `GroupBy(key)` | Single key, must be followed by a `Select`. |

### Terminals

| LINQ | Notes |
| --- | --- |
| `ToListAsync` / `ToArrayAsync` / `ToHashSetAsync` / `ToDictionaryAsync` / `ToLookupAsync` | List results. |
| `FirstAsync` / `FirstOrDefaultAsync` | The wire also carries an optional inline predicate. |
| `SingleAsync` / `SingleOrDefaultAsync` | The wire also carries an optional inline predicate. |
| `CountAsync` | No predicate overload yet — use `Where`. |
| `AnyAsync` | The wire also carries an optional inline predicate. |
| `ToPageAsync` | Scry-specific bounded [page envelope](paging.md). |

### Aggregates

`Count`, `Sum`, `Average`, `Min`, `Max` — only inside a projection over a `GroupBy`. See [grouping](querying.md#grouping-and-aggregates).

### Expression operators

`==` `!=` `<` `<=` `>` `>=` `&&` `||` `!` `+` `-` `*` `/` and unary `-`.

### Functions

String: `Contains`, `StartsWith`, `EndsWith`, `ToLower`, `ToUpper`, `string.IsNullOrEmpty`.
Date (`DateTime` / `DateOnly`): `Year`, `Month`, `Day`.


## Not yet supported

Everything below is translatable by EF Core but has no wire representation in Scry. Grouped by how well it fits the existing design.

### Likely additions

Fit the closed-vocabulary pattern — each is a new enum member or a small op record plus validator, builder, and generator work. Roughly ordered by expected demand.

- [ ] `Contains` over a client-side collection (`ids.Contains(_.Id)`, SQL `IN`) — the biggest practical gap for filtering.
- [ ] Top-level `Sum` / `Average` / `Min` / `Max` terminals (with selector), without requiring `GroupBy`.
- [ ] Predicate overload on `CountAsync`; `LongCountAsync`.
- [ ] `Distinct`.
- [ ] `All(predicate)`.
- [ ] `??` (coalesce) and `?:` (conditional) in expressions.
- [ ] `%` (modulo).
- [ ] More string functions: `Length`, `Trim` / `TrimStart` / `TrimEnd`, `Substring`, `IndexOf`, `Replace`, `string.IsNullOrWhiteSpace`.
- [ ] More date parts and members: `Hour`, `Minute`, `Second`, `DayOfWeek`, `DayOfYear`, `Date`, and the `Add*` methods.
- [ ] Math functions: `Abs`, `Ceiling`, `Floor`, `Round`.
- [ ] `ElementAtAsync` / `ElementAtOrDefaultAsync` (sugar over `Skip`/`Take`).
- [ ] `LastAsync` / `LastOrDefaultAsync` — EF requires an ordering; the validator would too.

### Needs design

Structurally bigger than the current pipeline — multiple sources per request, subqueries, or new type-surface in the generator. Each needs a security review before a wire shape.

- [ ] Collection navigations — currently not generated at all, so nothing can traverse into one. Prerequisite for most of the rest of this group.
- [ ] Subqueries in predicates (`_.Orders.Any(o => …)`, `_.Orders.Count > n`).
- [ ] `SelectMany`.
- [ ] `Join` / `GroupJoin` / `LeftJoin` / `RightJoin` / `FullJoin` — cross-source queries; interacts with per-source [row policies](policies.md), which must keep applying to every joined source.
- [ ] Set operations: `Union`, `Concat`, `Intersect`, `Except` — the request would carry more than one pipeline.
- [ ] Composite group keys; `Where` after `GroupBy` (SQL `HAVING`).
- [ ] `OfType` / `Cast` — inheritance hierarchies are not modelled in the generated surface.
- [ ] `DefaultIfEmpty`.
- [ ] `Reverse` — EF translates it only over an explicit ordering.
- [ ] Streaming results (`ToAsyncEnumerable`) — needs a streaming wire; see [Writing queries](querying.md#future-enhancements).

### Not gaps

Listed in EF's `QueryableMethods` but rejected by EF's own relational translation, so Scry not carrying them loses nothing:

`Aggregate`, `Zip`, `SequenceEqual`, `SkipWhile` / `TakeWhile`, `MaxBy` / `MinBy`, and every overload taking an `IEqualityComparer` / `IComparer`.

### Out of scope

Server-side EF surface that intentionally has no client-facing equivalent:

- **Write operations** (`ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`) — Scry is read-only.
- **Tracking and shaping** (`Include`, `AsNoTracking`, `AsSplitQuery`, …) — server execution details; clients shape results with `Select`.
- **Raw SQL** (`FromSql`, `EF.Functions.*`) — free-form SQL or provider functions from a hostile client is exactly what the closed vocabulary exists to prevent.
