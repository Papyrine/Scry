# LINQ coverage

What Scry supports compared to the LINQ surface EF Core can translate server-side, and — for everything left out — why. This page began as a roadmap as well as a reference. Nothing is left on it that is blocked on Scry's own design: what remains unsupported is waiting on the framework, excluded on purpose, or not asked for.

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
| `OfType<T>()` | Narrows to a derived type that is allow-listed in its own right; later operators read it. |
| `SelectMany(collection)` | Flattens a `[QueryableCollection]`; one per query, and later operators read the element. |
| `GroupBy(key)` | One key, or up to eight members grouped at once; each a member or an expression computed from the row. Must be followed by a `Select`. |
| `Distinct()` | Deduplicates the projected rows; can also be ordered, paged and counted over a flat projection of up to eight members. |
| `Reverse()` | Inverts the ordering; requires a preceding `OrderBy`, as EF does. |
| `Where(predicate)` after `GroupBy` | SQL `HAVING` — reads the group key and aggregates. |
| `Join(…)` / `LeftJoin(…)` / `RightJoin(…)` | Each side policy-filtered independently first; carries its own projection. A right join may not narrow its outer side. |
| `GroupJoin(…)` | Aggregating form only — the group is folded to a scalar, never projected, so the response stays flat. |
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
| `ToAsyncEnumerable` | [Streams](querying.md#streaming-rows) the rows; neither side holds the whole result. |
| `ToPageAsync` | Scry-specific bounded [page envelope](paging.md). |

### Aggregates

`Count`, `Sum`, `Average`, `Min`, `Max` are supported in two positions. As a projection value in the `Select` that follows a `GroupBy`, aggregating over the rows of each group — see [grouping](querying.md#grouping-and-aggregates):

<!-- snippet: clientGroupBy -->
<a id='snippet-clientGroupBy'></a>
```cs
regions = await Query
    .Order
    .GroupBy(_ => _.Region)
    .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
    .ToListAsync();
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L44-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientGroupBy' title='Start of snippet'>anchor</a></sup>
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

The other two positions EF Core allows an aggregate in are both reachable as well:

- Over an **opted-in collection navigation**, in a projection or anywhere else a value can appear — `.Select(_ => new { _.Name, Lines = _.Lines.Count() })`. Answered as a correlated subquery; see [collection subqueries](#collection-subqueries).
- In a **predicate** — `.Where(_ => _.Count() > 5)` after a `GroupBy`, which is SQL `HAVING`, and aggregate subqueries inside an ordinary `Where`.

### Expression operators

`==` `!=` `<` `<=` `>` `>=` `&&` `||` `!` `+` `-` `*` `/` `%` `??` `?:` and unary `-`. Where either operand is a string, `+` is concatenation and the other operand is converted by the database; `string.Concat` and a plain-hole interpolated string both mean the same thing.

### Functions

See [the full table](querying.md#functions). In summary — string: `Contains`, `StartsWith`, `EndsWith`, `ToLower`, `ToUpper`, `Length`, `Trim`/`TrimStart`/`TrimEnd`, `Substring`, `IndexOf`, `Replace`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, and `ToString()` for reading any other scalar as text. Date: `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `Millisecond`, `DayOfYear`, `DayOfWeek`, `Date`, and the `Add*` methods. Math: `Abs`, `Ceiling`, `Floor`, `Round`, `Truncate`, `Sqrt`, `Pow`, `Exp`, `Log`, `Log10`, and the trigonometric functions (`Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan`, `Atan2`). Plus `Contains` over a client-supplied set, which becomes a SQL `IN`.

Functions are expression-level: they read a row in a predicate, an ordering or group key, a terminal predicate, an aggregate selector, or a projection member.

The `StringComparison` overloads are supported through a **collation** rather than through the overload EF cannot translate: the request names a case sensitivity and the server maps it to a collation it configured. See [case sensitivity](querying.md#operators-1).

### Computed projection members

A projection leaf can be any of the above rather than only a member path — `Select(_ => new { Shouted = _.Name.ToUpper(), Net = _.Amount - _.Discount })`. See [projections](querying.md#computed-projection-members). The same holds inside a nested object, whose navigation is inferred from the paths an expression reads, and in a grouped `Select`, where the key and aggregates compose — `_.Sum(…) / _.Count()`.

A leaf must read at least one row member: a constant-only leaf is rejected, as EF refuses one in a client projection. A grouped leaf may still only read the key and aggregates, whether named directly or buried in an expression.

Reaching the wire is necessary but not sufficient: a function still has to be one the **EF provider** translates. Scry validates and rebinds it, then EF decides. Where a provider has no translation the query fails at execution rather than at validation — so a function with no SQL Server translation is left out of the closed set rather than shipped as a trap.


## Not supported

Nothing here is blocked on Scry's design. Each item is left out for a stated reason.

### Waiting on the framework or the provider

Nothing to design; the surface underneath does not carry them.

- `FullJoin` — `Queryable.FullJoin` is a .NET 11 addition. On net10 the client cannot express it and EF cannot execute it, so there is nothing to carry. The wire deliberately does not reserve a join kind for it: an operator the server would only reject is not worth committing to the contract.
- `ToString(format)`, and the interpolated `$"{value:N2}"`. No provider translates it — EF's converter takes the argument-less form only — and the SQL function that would express it, `FORMAT`, reads the server's language, so the same row would format differently per connection. Unlike [`DayOfWeek`](querying.md#functions) there is no deterministic composition to build instead. It appears to work in a projection only because EF evaluates it client-side once the rows are read; the same expression in a `Where`, `OrderBy` or `GroupBy` fails. See [reading a value as text](querying.md#reading-a-value-as-text).
- `Math.Sign` — the provider translates it, but SQL's `SIGN` returns its argument's type while `Math.Sign` returns `int`, so the result does not materialize: a query using it succeeds in a predicate, where nothing is read back, and faults in a projection. Translating is not sufficient on its own, and unlike [`DayOfWeek`](querying.md#functions) there is no deterministic composition to build instead — the translation is right, only its result type is unreadable.

### Deliberately left out

Considered and rejected, for reasons that have not changed.

- `Cast<T>` — a cast asserts a type rather than filtering to it, so a row of the wrong type has no answer. [`OfType`](querying.md#narrowing-to-a-derived-type) is the operator that means "the ones that are".
- `DefaultIfEmpty` — its only use in LINQ is expressing an outer join, which `LeftJoin`, `RightJoin` and `GroupJoin` now do directly. Standalone it yields an all-null row, which a projected response has no use for.
- `GroupJoin` **projecting** its group, and `SelectMany` with a **result selector**. The first would put a nested collection in a response, which is exactly what keeps collections [aggregable and not projectable](annotations.md#collections); the second would produce a two-rooted row without a join's projection to name the sides. Both have a supported form: aggregate the group, or flatten first and then `Select`.

### Not gaps

Listed in EF's `QueryableMethods` but rejected by EF's own relational translation, so Scry not carrying them loses nothing:

`Aggregate`, `Zip`, `SequenceEqual`, `SkipWhile` / `TakeWhile`, `MaxBy` / `MinBy`, and every overload taking an `IEqualityComparer` / `IComparer`.

### Out of scope

Server-side EF surface that intentionally has no client-facing equivalent:

- **Write operations** (`ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`) — Scry is read-only.
- **Tracking and shaping** (`Include`, `AsNoTracking`, `AsSplitQuery`, …) — server execution details; clients shape results with `Select`.
- **Raw SQL** (`FromSql`, `EF.Functions.*`) — free-form SQL or provider functions from a hostile client is exactly what the closed vocabulary exists to prevent.
