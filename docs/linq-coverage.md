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
| `GroupBy(key)` | One key, or up to eight members grouped at once. Must be followed by a `Select`. |
| `Distinct()` | Deduplicates the projected rows; can also be ordered, paged and counted over a flat projection of up to eight members. |
| `Reverse()` | Inverts the ordering; requires a preceding `OrderBy`, as EF does. |
| `Where(predicate)` after `GroupBy` | SQL `HAVING` — reads the group key and aggregates. |
| `Join(…)` / `LeftJoin(…)` | Each side policy-filtered independently first; carries its own projection. |
| `Union` / `Concat` / `Intersect` / `Except` | Each side policy-filtered first; both project the same shape. |

### Membership of another source

`Contains` over a query against a second source becomes a SQL `IN (SELECT …)`. That source is resolved and policy-filtered before the test, the same way a join resolves its second side, so membership is only ever of rows the caller could have queried directly. See [membership of another source](querying.md#membership-of-another-source).

### Collection subqueries

A collection navigation opted in with [`[QueryableCollection]`](annotations.md#collections) is **aggregable, not projectable**: `Any`, `All`, `Count`, `Sum`, `Average`, `Min`, `Max` over it, answered as a correlated subquery, in any position a value can appear. Its rows can never be enumerated, so a response never carries a nested collection. See [collection subqueries](querying.md#collection-subqueries).

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

<!-- snippet: clientAggregateTerminal -->
<a id='snippet-clientAggregateTerminal'></a>
```cs
var sum = await client.Source<Order>("Order")
    .Where(_ => _.Region == "North")
    .SumAsync(_ => _.Amount);
```
<sup><a href='/src/Scry.Tests/ExpandedOperatorTests.cs#L136-L140' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientAggregateTerminal' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The remaining positions an aggregate can appear in EF Core have no wire representation:

- Over a **collection navigation in a projection** — `.Select(_ => new { _.Name, Orders = _.Orders.Count() })`. Collection navigations are not exposed at all.
- In a **predicate** — `.Where(g => g.Count() > 5)` after a `GroupBy` (SQL `HAVING`), or any aggregate-based subquery inside a `Where`.

Both are tracked in [Not yet supported](#not-yet-supported).

### Expression operators

`==` `!=` `<` `<=` `>` `>=` `&&` `||` `!` `+` `-` `*` `/` `%` `??` `?:` and unary `-`. Over two strings `+` is concatenation, which `string.Concat` and a plain-hole interpolated string both lower to.

### Functions

See [the full table](querying.md#functions). In summary — string: `Contains`, `StartsWith`, `EndsWith`, `ToLower`, `ToUpper`, `Length`, `Trim`/`TrimStart`/`TrimEnd`, `Substring`, `IndexOf`, `Replace`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`. Date: `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `Millisecond`, `DayOfYear`, `Date`, and the `Add*` methods. Math: `Abs`, `Ceiling`, `Floor`, `Round`, `Truncate`, `Sqrt`, `Pow`. Plus `Contains` over a client-supplied set, which becomes a SQL `IN`.

Functions are expression-level: they read a row in a predicate, an ordering or group key, a terminal predicate, an aggregate selector, or a projection member.

The `StringComparison` overloads are supported through a **collation** rather than through the overload EF cannot translate: the request names a case sensitivity and the server maps it to a collation it configured. See [case sensitivity](querying.md#operators-1).

### Computed projection members

A projection leaf can be any of the above rather than only a member path — `Select(_ => new { Shouted = _.Name.ToUpper(), Net = _.Amount - _.Discount })`. See [projections](querying.md#computed-projection-members). The same holds inside a nested object, whose navigation is inferred from the paths an expression reads, and in a grouped `Select`, where the key and aggregates compose — `_.Sum(…) / _.Count()`.

A leaf must read at least one row member: a constant-only leaf is rejected, as EF refuses one in a client projection. A grouped leaf may still only read the key and aggregates, whether named directly or buried in an expression.

Reaching the wire is necessary but not sufficient: a function still has to be one the **EF provider** translates. Scry validates and rebinds it, then EF decides. Where a provider has no translation the query fails at execution rather than at validation — so a function with no SQL Server translation is left out of the closed set rather than shipped as a trap.


## Not yet supported

Everything below is translatable by EF Core but has no wire representation in Scry. Grouped by how well it fits the existing design.

### Likely additions

Fit the closed-vocabulary pattern — each is a new enum member or a small op record plus validator, builder, and generator work. Roughly ordered by expected demand.

- [ ] `DayOfWeek` — deliberately omitted, not overlooked: SQL Server's provider has no translation for it, so it would compile client-side and then fail at execution. Worth adding behind provider capability detection, or when a supporting provider is targeted.
- [ ] The rest of the SQL Server math surface — `Exp`, `Log`, `Log10`, `Sign`, and the trig functions. All are translated by the provider; none has been asked for yet.

### Needs design

Structurally bigger than the current pipeline — multiple sources per request, subqueries, or new type-surface in the generator. Each needs a security review before a wire shape, and the notes below are that review as far as it has been taken.

- [ ] `SelectMany` — needs collections *projectable*, which the shipped design deliberately excludes; revisit only with a bounded nested-result shape.

**Cross-source queries.** `Join`, `LeftJoin` and the [set operations](querying.md#set-operations) are supported. The policy composition they were gated on is settled: each side is resolved and policy-filtered independently before the two meet, so a join can only narrow. 

- [x] `Join` / `LeftJoin` / `RightJoin`. A right join widens the *outer* side instead of the inner, and refuses a narrowed outer side — see [Joins](querying.md#joins).
- [ ] `FullJoin` — blocked on the framework, not on design: `Queryable.FullJoin` is a .NET 11 addition, so on net10 the client cannot express it and EF cannot execute it.
- [ ] `GroupJoin` — returns a collection per outer row, which the [collections design](annotations.md#collections) deliberately keeps unprojectable.
- [ ] `DefaultIfEmpty` — only meaningful alongside joins.

**Other.**

- [ ] `OfType` / `Cast` — inheritance hierarchies are not modelled in the generated surface at all: one query model is emitted per opted-in type with no relationship between them.
- [ ] Streaming results (`ToAsyncEnumerable`) — needs a streaming wire; see [Writing queries](querying.md#future-enhancements).

### Not gaps

Listed in EF's `QueryableMethods` but rejected by EF's own relational translation, so Scry not carrying them loses nothing:

`Aggregate`, `Zip`, `SequenceEqual`, `SkipWhile` / `TakeWhile`, `MaxBy` / `MinBy`, and every overload taking an `IEqualityComparer` / `IComparer`.

### Out of scope

Server-side EF surface that intentionally has no client-facing equivalent:

- **Write operations** (`ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`) — Scry is read-only.
- **Tracking and shaping** (`Include`, `AsNoTracking`, `AsSplitQuery`, …) — server execution details; clients shape results with `Select`.
- **Raw SQL** (`FromSql`, `EF.Functions.*`) — free-form SQL or provider functions from a hostile client is exactly what the closed vocabulary exists to prevent.
