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
| `Distinct()` | Deduplicates the projected rows; only the `Select` and a terminal may follow. |

### Terminals

| LINQ | Notes |
| --- | --- |
| `ToListAsync` / `ToArrayAsync` / `ToHashSetAsync` / `ToDictionaryAsync` / `ToLookupAsync` | List results. |
| `FirstAsync` / `FirstOrDefaultAsync` | Optional predicate. |
| `SingleAsync` / `SingleOrDefaultAsync` | Optional predicate. |
| `LastAsync` / `LastOrDefaultAsync` | Optional predicate. Requires an ordered query, as EF does. |
| `ElementAtAsync` / `ElementAtOrDefaultAsync` | `Skip` + `First`; no wire operator of its own. |
| `CountAsync` / `LongCountAsync` | Optional predicate. |
| `AnyAsync` | Optional predicate. |
| `AllAsync(predicate)` | |
| `SumAsync` / `AverageAsync` / `MinAsync` / `MaxAsync` | Over the whole sequence, no `GroupBy` needed. |
| `ToPageAsync` | Scry-specific bounded [page envelope](paging.md). |

### Aggregates

`Count`, `Sum`, `Average`, `Min`, `Max` are supported in two positions. As a projection value in the `Select` that follows a `GroupBy`, aggregating over the rows of each group — see [grouping](querying.md#grouping-and-aggregates):

<!-- snippet: clientGroupBy -->
<a id='snippet-clientGroupBy'></a>
```cs
regions = await Query.Order
    .GroupBy(_ => _.Region)
    .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
    .ToListAsync();
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L43-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientGroupBy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

…and as a **terminal folding the whole sequence** to one scalar, which needs no `GroupBy`:

```cs
var total = await Query.Order
    .Where(_ => _.Region == "North")
    .SumAsync(_ => _.Amount);
```

The remaining positions an aggregate can appear in EF Core have no wire representation:

- Over a **collection navigation in a projection** — `.Select(_ => new { _.Name, Orders = _.Orders.Count() })`. Collection navigations are not exposed at all.
- In a **predicate** — `.Where(g => g.Count() > 5)` after a `GroupBy` (SQL `HAVING`), or any aggregate-based subquery inside a `Where`.

Both are tracked in [Not yet supported](#not-yet-supported).

### Expression operators

`==` `!=` `<` `<=` `>` `>=` `&&` `||` `!` `+` `-` `*` `/` `%` `??` `?:` and unary `-`.

### Functions

See [the full table](querying.md#functions). In summary — string: `Contains`, `StartsWith`, `EndsWith`, `ToLower`, `ToUpper`, `Length`, `Trim`/`TrimStart`/`TrimEnd`, `Substring`, `IndexOf`, `Replace`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`. Date: `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `DayOfYear`, `Date`, and the `Add*` methods. Math: `Abs`, `Ceiling`, `Floor`, `Round`. Plus `Contains` over a client-supplied set, which becomes a SQL `IN`.

Functions are expression-level: they read a row in a predicate, an ordering or group key, a terminal predicate, an aggregate selector, or a projection member.

### Computed projection members

A projection leaf can be any of the above rather than only a member path — `Select(_ => new { Shouted = _.Name.ToUpper(), Net = _.Amount - _.Discount })`. See [projections](querying.md#computed-projection-members). A leaf must read at least one row member (a constant-only leaf is rejected, as EF refuses one in a client projection), a member of a *nested* object must stay a plain path, and a grouped `Select` is still limited to the group key and aggregates.

Reaching the wire is necessary but not sufficient: a function still has to be one the **EF provider** translates. Scry validates and rebinds it, then EF decides. Where a provider has no translation the query fails at execution rather than at validation — so a function with no SQL Server translation is left out of the closed set rather than shipped as a trap.


## Not yet supported

Everything below is translatable by EF Core but has no wire representation in Scry. Grouped by how well it fits the existing design.

### Likely additions

Fit the closed-vocabulary pattern — each is a new enum member or a small op record plus validator, builder, and generator work. Roughly ordered by expected demand.

- [ ] Expressions in a **nested** projection member and in a **grouped** `Select` — the two positions a computed leaf still cannot appear. The first needs the navigation a nested object descends into to be carried explicitly rather than inferred from its member paths; the second needs aggregates to compose (`_.Sum(…) / _.Count()`), which the grouped builder handles only as whole members today.
- [ ] Ordering a deduplicated query — `Distinct().OrderBy(…)`, and paging over it. Both need ordering keys expressed against the *projection* rather than the row, which is also what would let `Skip`/`Take` follow a `Distinct`.
- [ ] Counting a `Distinct` over more than one projected member. `COUNT(DISTINCT x)` is single-column in SQL; a multi-column form needs a projection type with real equality, which the shaped `object[]` row deliberately is not.
- [ ] `string.Concat` / interpolation, `char` members, `StartsWith` with a `StringComparison`.
- [ ] `DayOfWeek` — deliberately omitted, not overlooked: SQL Server's provider has no translation for it, so it would compile client-side and then fail at execution. Worth adding behind provider capability detection, or when a supporting provider is targeted.
- [ ] `Math.Pow` / `Sqrt` / `Truncate`, and the remaining sub-second date parts (`Millisecond`).

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
- [ ] `Contains` over a **server-side** sequence (a subquery `IN`) rather than a client-supplied set.
- [ ] Streaming results (`ToAsyncEnumerable`) — needs a streaming wire; see [Writing queries](querying.md#future-enhancements).

### Not gaps

Listed in EF's `QueryableMethods` but rejected by EF's own relational translation, so Scry not carrying them loses nothing:

`Aggregate`, `Zip`, `SequenceEqual`, `SkipWhile` / `TakeWhile`, `MaxBy` / `MinBy`, and every overload taking an `IEqualityComparer` / `IComparer`.

### Out of scope

Server-side EF surface that intentionally has no client-facing equivalent:

- **Write operations** (`ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`) — Scry is read-only.
- **Tracking and shaping** (`Include`, `AsNoTracking`, `AsSplitQuery`, …) — server execution details; clients shape results with `Select`.
- **Raw SQL** (`FromSql`, `EF.Functions.*`) — free-form SQL or provider functions from a hostile client is exactly what the closed vocabulary exists to prevent.
