# LINQ coverage

What Scry supports compared to the LINQ surface EF Core can translate server-side, and — for everything left out — why. This page began as a roadmap as well as a reference. Nothing is left on it that is blocked on Scry's own design: what remains unsupported is waiting on the framework, excluded on purpose, or not asked for.

Scry's wire vocabulary is a **deliberately closed set**. Every operator, function, and expression node must be individually representable, validatable, and rebindable — see the [security model](security.md). So the goal is not parity with EF Core; it is covering the operations remote clients actually need, one auditable addition at a time. Anything outside the set is refused twice over: the vocabulary itself by the client's translator, which throws `NotSupportedException` before a request is sent, and the composition rules — one `Select`, one join, `Reverse` only over an ordering — by the server, which answers 400. An [analyzer](#reported-at-compile-time) reports both at the call site first.

For usage detail on the supported surface (position rules, limits, examples), see [Writing queries](querying.md).


## Reported at compile time

`Scry.Client` ships a Roslyn analyzer in the same assembly as the [source generator](source-generator.md), so it needs no second package reference and nothing to switch on. It reads LINQ written against a generated query model — or against a hand-built source opened through the client — and reports what the closed set cannot carry, where it was written, with the reasoning on this page linked from each diagnostic.

| Rule | Reports |
| --- | --- |
| `SCRY100` | An operator outside the set, or an overload of one that is in it — `SkipWhile`, `Chunk`, `Select` with an index |
| `SCRY101` | [`Cast<T>`](#deliberately-left-out), naming `OfType<T>` as the operator that narrows |
| `SCRY102` | [`SelectMany` with a result selector](#deliberately-left-out) |
| `SCRY103` | An `IComparer` or `IEqualityComparer` overload |
| `SCRY104` | A second `Select`, `Distinct`, `GroupBy`, `SelectMany` or join |
| `SCRY105` | An [ordering key that constructs an object](#anonymous-types) |
| `SCRY106` | A projection that does not construct one |
| `SCRY107` | A function outside [the closed set](querying.md#functions) |
| `SCRY108` | [`ToString(format)`](#waiting-on-the-framework-or-the-provider), and the interpolated hole that means the same |
| `SCRY109` | A synchronous terminal — `ToList`, `First`, `Count` — naming the async one that replaces it, or a synchronous `foreach` over the query itself (an `await foreach` over [`ToAsyncEnumerable`](querying.md#streaming-rows) is the supported form and is left alone) |
| `SCRY110` | `Reverse` with no preceding `OrderBy` |
| `SCRY111` | A `GroupJoin` that projects its group rather than folding it |
| `SCRY112` | Client-side code reading the row — a helper, a `Parse`, an extension method, a delegate — which has no wire representation at all |

All of them are warnings. A query the analyzer cannot read is still refused by the translator or the server exactly as before, so a rule that is allowed to be incomplete never breaks a build on its own. To make the set an error:

```ini
# .editorconfig
[*.cs]
dotnet_analyzer_diagnostic.category-Scry.severity = error
```

### What it deliberately does not check

The analyzer reads the chain as written, and holds precision above recall — reporting working code is the worse failure, since everything it misses is caught twice downstream.

- **Chains it cannot follow.** A query composed across statements is followed through the locals holding it; one assembled through a helper method, a conditional, or a reassigned local is not.
- **Sequences in a query lambda.** `Select`, `Count` and `Contains` all mean something else inside one — a membership test against another source, a correlated subquery over a collection navigation, an aggregate over a group — so anything called on a sequence is left to the translator. What is checked is calls that read the row itself: members of a string, a date, or `Math` against the callable set, and everything else as client-side code. A value that comes from closure state is evaluated into a constant before it reaches the wire and is left alone.
- **Values known only when the query runs.** `Take` against `MaxPageSize`, the size of a `Contains` set, and whether the other side of a join is a Scry source at all.
- **Whether the provider can translate it.** Reaching the wire is necessary but not sufficient, [as above](#computed-projection-members).

It is not a security boundary and cannot become one. The client is assumed hostile, the analyzer runs on the client's own build, and the server re-validates every request against its own allow-list regardless — see the [security model](security.md). This moves a mistake from a stack trace to a squiggle, and nowhere else.


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
| `SelectMany(collection)` | Flattens a `[QueryableCollection]` of rows; one per query, and later operators read the element. A collection of values cannot be flattened. |
| `GroupBy(key)` | One key, or up to eight members grouped at once; each a member or an expression computed from the row. Must be followed by a `Select`. A result selector — `GroupBy(key, (key, group) => …)` — unfolds into that `GroupBy` + `Select`, and counts as the query's one `Select`. |
| `Distinct()` | Deduplicates the projected rows; can also be ordered, paged and counted over a flat projection of up to eight members. |
| `Reverse()` | Inverts the ordering; requires a preceding `OrderBy`, as EF does. |
| `Where(predicate)` after `GroupBy` | SQL `HAVING` — reads the group key and aggregates. |
| `Join(…)` / `LeftJoin(…)` / `RightJoin(…)` | Each side policy-filtered independently first; carries its own projection. Keys may be composite — `new {_.A, _.B}` on both sides, compared part by part. The inner side may filter, and order bounded by `Skip`/`Take`. A right join may not narrow its outer side. |
| `GroupJoin(…)` | Aggregating form only — the group is folded to a scalar, never projected, so the response stays flat. |
| `Union` / `Concat` / `Intersect` / `Except` | Each side policy-filtered first; both project the same shape. An operand may filter, and order bounded by `Skip`/`Take`, before its `Select`. |

### Membership of another source

`Contains` over a query against a second source becomes a SQL `IN (SELECT …)`. That source is resolved and policy-filtered before the test, the same way a join resolves its second side, so membership is only ever of rows the caller could have queried directly. See [membership of another source](querying.md#membership-of-another-source).

### Collection subqueries

A collection opted in with [`[QueryableCollection]`](annotations.md#collections) is **aggregable, not projectable**: `Any`, `All`, `Count`, `Sum`, `Average`, `Min`, `Max` over it, answered as a correlated subquery, in any position a value can appear. Its rows can never be enumerated, so a response never carries a nested collection. See [collection subqueries](querying.md#collection-subqueries).

A collection of **values** — an EF primitive collection, or a JSON array of `[QueryableComplex]` value objects — answers the same set. For a collection of values the element has no members, so the predicate and selector read the element itself, and `Contains` is available as the `Any(_ => _ == value)` it stands for. See [collections of values](querying.md#collections-of-values).

### Terminals

| LINQ | Notes |
| --- | --- |
| `ToListAsync` / `ToArrayAsync` / `ToHashSetAsync` / `ToDictionaryAsync` / `ToLookupAsync` | List results. |
| `FirstAsync` / `FirstOrDefaultAsync` | Optional predicate. |
| `SingleAsync` / `SingleOrDefaultAsync` | Optional predicate. |
| `LastAsync` / `LastOrDefaultAsync` | Optional predicate. Requires an ordered query, as EF does. |
| `ElementAtAsync` / `ElementAtOrDefaultAsync` | `Skip` + `First`; no wire operator of its own. |
| `MaxByAsync` / `MinByAsync` (+ `OrDefault` forms) | `OrderBy` + `First`; no wire operator of its own. The key is a single value read off the row, before any projection. |
| `CountAsync` / `LongCountAsync` | Optional predicate. |
| `AnyAsync` | Optional predicate. |
| `AllAsync(predicate)` | |
| `SumAsync` / `AverageAsync` / `MinAsync` / `MaxAsync` | Over the whole sequence, no `GroupBy` needed. |
| `ToAsyncEnumerable` | [Streams](querying.md#streaming-rows) the rows; neither side holds the whole result. |
| `ToPageAsync` | Scry-specific bounded [page envelope](paging.md). |

### Aggregates

`Count`, `Sum`, `Average`, `Min`, `Max` are supported in two positions, and `string.Join` over the group's values — SQL's `STRING_AGG`, with `string.Concat` as its empty-separator spelling — in the grouped one alone. The joined values are ordered by themselves, since SQL leaves the concatenation order unspecified, so the same answer reads from any source. As a projection value in the `Select` that follows a `GroupBy`, aggregating over the rows of each group — see [grouping](querying.md#grouping-and-aggregates):

<!-- snippet: clientGroupBy -->
<a id='snippet-clientGroupBy'></a>
```cs
regions = await Query
    .Order
    .GroupBy(_ => _.Region)
    .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
    .ToListAsync();
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L57-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientGroupBy' title='Start of snippet'>anchor</a></sup>
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

`==` `!=` `<` `<=` `>` `>=` `&&` `||` `!` `+` `-` `*` `/` `%` `??` `?:` and unary `-`. Where either operand is a string, `+` is concatenation and the other operand is converted by the database; `string.Concat` and a plain-hole interpolated string both mean the same thing. `Equals` is the `==` comparison spelled as a method and carried as one, over any scalar and in either spelling — as are an optional member's `Value` and `HasValue`, which mean the member itself and `!= null`.

### Functions

See [the full table](querying.md#functions). In summary — string: `Contains`, `StartsWith`, `EndsWith`, `ToLower`, `ToUpper`, `Length`, `Trim`/`TrimStart`/`TrimEnd`, `Substring`, `IndexOf`, `Replace`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, `FirstOrDefault`/`LastOrDefault` for its end characters, and `ToString()` for reading any other scalar as text. Date: `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `Millisecond`, `Microsecond`, `Nanosecond`, `DayOfYear`, `DayOfWeek`, `DayNumber`, `Date`, `TimeOfDay`, and the `Add*` methods — plus the parts of an elapsed time (`Hours`, `Minutes`, … in the plural), the conversions between the temporal types, and Unix time. Binary: `Length`, `Contains`, `First`/`ElementAt`. Math: `Abs`, `Ceiling`, `Floor`, `Round`, `Truncate`, `Sqrt`, `Pow`, `Sign`, `Exp`, `Log`, `Log10`, and the trigonometric functions (`Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan`, `Atan2`). Plus `Contains` over a client-supplied set, which becomes a SQL `IN`, and `HasFlag` over a `[Flags]` enum member.

Functions are expression-level: they read a row in a predicate, an ordering or group key, a terminal predicate, an aggregate selector, or a projection member.

The `StringComparison` overloads are supported through a **collation** rather than through the overload EF cannot translate: the request names a case sensitivity and the server maps it to a collation it configured. See [case sensitivity](querying.md#operators-1).

### Anonymous types

An object a query constructs can be an **anonymous type**, a record, or an object initializer — the three spell the same wire projection, and every position LINQ constructs one in takes any of them:

- A **`Select`** — `Select(_ => new { _.Name, Manager = _.Manager!.Name })`. The member names come off the anonymous type; a record or constructor call takes them from the constructor parameter names, capitalized. See [projections](querying.md#projections).
- **Nested inside a projection** — `Select(_ => new { _.Name, Department = new { _.Department!.Name } })`, which nests the result under that member rather than flattening it. One level, and every member sharing one navigation. See [nested result objects](querying.md#nested-result-objects).
- A **composite `GroupBy` key** — `GroupBy(_ => new { _.Region, _.Grade })`. Each part keeps the name the key type gave it, so `_.Key.Region` in the following `Select` resolves back to the member it grouped by. See [grouping](querying.md#grouping-and-aggregates).
- A **join result selector** — `(outer, inner) => new { … }`, each leaf a member path naming the side it reads. See [joins](querying.md#joins).

The result reads back into the anonymous type on the client: a response is keyed by member name, so it materializes exactly as a record or a named class does.

Nowhere else. An anonymous type has no ordering of its own and the wire carries no constructed value outside a projection, so `OrderBy(_ => new { … })` is rejected at translation time — order by one key and add the rest with `ThenBy` ([ordering rules](querying.md#ordering-rules)).

### Computed projection members

A projection leaf can be any of the above rather than only a member path — `Select(_ => new { Shouted = _.Name.ToUpper(), Net = _.Amount - _.Discount })`. See [projections](querying.md#computed-projection-members). The same holds inside a nested object, whose navigation is inferred from the paths an expression reads, and in a grouped `Select`, where the key and aggregates compose — `_.Sum(…) / _.Count()`.

A leaf must read at least one row member: a constant-only leaf is rejected, as EF refuses one in a client projection. A grouped leaf may still only read the key and aggregates, whether named directly or buried in an expression.

Reaching the wire is necessary but not sufficient: a function still has to be one the **EF provider** translates. Scry validates and rebinds it, then EF decides. Where a provider has no translation the query fails at execution rather than at validation — so a function with no SQL Server translation is left out of the closed set rather than shipped as a trap.


## Not supported

Nothing here is blocked on Scry's design. Each item is left out for a stated reason.

### Waiting on the framework or the provider

Nothing to design; the surface underneath does not carry them.

- `FullJoin` — `Queryable.FullJoin` is a .NET 11 addition. On net10 the client cannot express it and EF cannot execute it, so there is nothing to carry. The wire deliberately does not reserve a join kind for it: an operator the server would only reject is not worth committing to the contract.
- **A string read by position** — the `s[i]` indexer. EF translates `FirstOrDefault()` and `LastOrDefault()` on a string, which [are carried](querying.md#functions), but has no translation for the indexer between them, and an index past the end would fault where those two answer with the default.
- **`byte[].Any()`.** The SQL Server provider carries a translation for it — `DATALENGTH(…) > 0` — and refuses the expression before reaching it, so the spelling that works is that comparison written out: `_.Signature.Length > 0`.
- **The `DateTime` readings of a `DateTimeOffset`** — `DateTime`, `UtcDateTime`, `LocalDateTime`. The provider's translation is conditioned on the column's store type and does not fire for an ordinary mapped member, so each is a query that validates and then fails at execution. `LocalDateTime` is doubly out: it reads `CURRENT_TIMEZONE_ID()`, the server's own zone, so the same row would answer differently on two machines — the objection that keeps [`DayOfWeek`](#carried-here-refused-there)'s obvious formulation out as well. The parts of an offset (`Year`, `Hour`, …) and its [Unix-time readings](querying.md#functions) are unaffected and carried.
- `ToString(format)`, and the interpolated `$"{value:N2}"`. No provider translates it — EF's converter takes the argument-less form only — and the SQL function that would express it, `FORMAT`, reads the server's language, so the same row would format differently per connection. Unlike [`DayOfWeek`](querying.md#functions) and [`Math.Sign`](querying.md#functions), where a deterministic composition existed to build instead, here there is none. It appears to work in a projection only because EF evaluates it client-side once the rows are read; the same expression in a `Where`, `OrderBy` or `GroupBy` fails. See [reading a value as text](querying.md#reading-a-value-as-text).

### Deliberately left out

Considered and rejected, for reasons that have not changed.

- `Cast<T>` — not [`OfType<T>`](querying.md#narrowing-to-a-derived-type) under another name. `OfType` **filters**: a row that is not a `T` is left out. `Cast` **asserts**: every row is required to be a `T` already, and one that is not is an error rather than an omission.

  EF does carry that assertion — but in the *materializer*, as the check that runs while a row is turned into an entity. A Scry query never gets there. It always ends in a projection to a shaped row, so no entity is ever constructed and the check never runs.

  What is left is worse than either operator. Reading the derived type's members off rows that are not of that type is, under table-per-hierarchy, a read of columns that are null for those rows — so the query neither filters nor faults, and answers with exactly the rows the assertion existed to rule out. Casting the sample's assets to `Vehicle` returns all four, the building and the artwork included, each with a null where the vehicle's own member should be. `OfType` narrows by filtering, needs nothing on the way back, and is rejected up front when the type is not allow-listed.

  Casting the other way — up to a base — is well defined and asks for nothing. The wire names a *source*, never a CLR type, and a response is projected by member name, so an upcast alters only the client's static view of rows it was already receiving.

- `DefaultIfEmpty` — translatable, and left out anyway. Its purpose in LINQ is the outer-join idiom: `GroupJoin`, then `SelectMany` over the group with `DefaultIfEmpty` on it, which is how a left outer join was spelled before there was an operator for one. [`LeftJoin`, `RightJoin` and `GroupJoin`](querying.md#joins) now say it directly, and Scry could not spell the idiom regardless — it needs `SelectMany` with a result selector over a group, which is excluded in the entry below.

  Standalone it asks something else: yield one row when the result would otherwise be empty. EF does translate that, so the objection is not that it cannot be carried. It is what arrives. The row is `null` — an absent row, not a row of default values — and a Scry response is a list of projected objects, so it lands as a null element in the client's list. Every row would need a null check before use, to learn the one thing the feature conveys: that the result was empty. An empty list already says that, in the shape the client handles anyway.

  The overload that supplies the fallback has nowhere to put it. The wire carries scalar constants, never a constructed row, so there is no way to name what the default should be.
- `GroupJoin` **projecting** its group, and `SelectMany` with a **result selector**. The first would put a nested collection in a response, which is exactly what keeps collections [aggregable and not projectable](annotations.md#collections); the second would produce a two-rooted row without a join's projection to name the sides. Both have a supported form: aggregate the group, or flatten first and then `Select`.

### Not gaps

Listed in EF's `QueryableMethods`, and on `Queryable` rather than only `Enumerable` — so they reach EF rather than quietly enumerating client-side — but rejected by its relational translation. Each throws *could not be translated* against a real database, so Scry not carrying them loses nothing:

`Aggregate`, `Zip`, `SequenceEqual`, `SkipWhile` / `TakeWhile`, `MaxBy` / `MinBy` (carried instead as the [`MaxByAsync` / `MinByAsync` terminals](#terminals) — the same `OrderBy` + `First` rewrite EF 11 adopts for the `Queryable` forms), and every overload taking an `IEqualityComparer` / `IComparer`.

### Out of scope

Server-side EF surface that intentionally has no client-facing equivalent:

- **Write operations** (`ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`) — Scry is read-only.
- **Tracking and shaping** (`Include`, `AsNoTracking`, `AsSplitQuery`, …) — server execution details; clients shape results with `Select`.
- **Raw SQL** (`FromSql`, `EF.Functions.*`) — free-form SQL or provider functions from a hostile client is exactly what the closed vocabulary exists to prevent.


## Measured against EF Core

The closed set is a subset of what EF Core can translate — necessarily, since the server rebinds every query onto EF to execute it. This section itemizes the rest of EF's surface, from its own source (the relational query pipeline and the SQL Server provider's translators, surveyed against the EF 11 previews), sorted by what each item's absence means. Together with the sections above, it accounts for the whole distance between the two.

### Carried here, refused there

The subset relation runs the other way in a few places. Each is an instance of the wire carrying *intent* where EF is handed a CLR construct it will not guess about:

- **`DayOfWeek`.** EF refuses it outright: `DATEPART(weekday, …)` reads `@@DATEFIRST`, a session setting, so the same row would answer differently on two connections. Scry counts whole days from a fixed Monday and takes the remainder, which depends on nothing but the date — see [functions](querying.md#functions).
- **The `StringComparison` overloads.** EF fails them with a dedicated error. Scry reads the one thing they can mean on a database — a case sensitivity — and maps it to a [collation](querying.md#operators-1) the server configured.
- **Interpolated strings.** In an expression tree an interpolation lowers to `string.Format`, which EF does not translate. Scry rewrites the plain-hole form into the concatenation it means.

### Normalizable into the existing vocabulary

Sugar EF unfolds into operators the wire already carries, adopted as client-side rewrites with no wire change: `GroupBy(key, resultSelector)`, `Nullable<T>.GetValueOrDefault()`, and the `MaxByAsync` / `MinByAsync` terminals — the `OrderBy` + `First` unfolding EF 11 itself applies to `MaxBy` / `MinBy` — alongside the older precedent of `ElementAtAsync`, which is `Skip` + `First` under the covers.

Two more join them, each the same rewrite EF's own translators perform:

- **`Equals`** is `==` written as a method, in both spellings and over any scalar, and becomes the same comparison node. The `StringComparison` overloads are the exception and mean something else — a case sensitivity, read as a [collation](querying.md#operators-1) — including the static three-argument form.
- **`Nullable<T>.Value` and `HasValue`.** Every wire operand is already optional, so `Value` is the member it wraps and `HasValue` is `!= null`. Carried as path segments they read as members no source has, which is how they used to fail: server-side, and named as a traversal error rather than as the unsupported members they were.

### Room to grow

Translatable by EF, compatible with the wire model, and absent only because nothing has asked for it yet. Functions with an EF translation ready to rebind onto:

| Group | Candidates |
| --- | --- |
| String | `IndexOf(value, start)`, `TrimStart` / `TrimEnd` with explicit characters (SQL Server 2022+), `string.Join` over row values (`CONCAT_WS`). The `char` overloads of the single-argument functions are already carried — a char constant travels as text — and `Compare` / `CompareTo` are carried as the `CompareTo` function. |
| Temporal | `TimeOnly.IsBetween`, and date difference — EF blocks `d1 - d2` arithmetic outright, so the translatable spelling is a dedicated function the server would rebind to `EF.Functions.DateDiff*` without exposing `EF.Functions` itself |

A member of a type the wire carries but has no function for is not refused by the translator: it reads as an ordinary path segment, and is rejected server-side as a member the source does not have. The [analyzer](#reported-at-compile-time) reports it at the call site first, for every type whose members it measures against the callable set — `_.Duration.TotalHours` is `SCRY107`.

One operator belongs here too: `Order()` / `OrderDescending()`, the key-less orderings EF added in 8. They order by the element itself, so they need a sequence of scalars to be useful — and a Scry query's one `Select` [must construct an object](#anonymous-types), which is why nothing has asked for them.

Environmental values — `DateTime.Now`, `Guid.NewGuid()`, `EF.Functions.Random()` — are representable but unmotivated: a closure's `DateTime.Now` already travels as the constant it evaluates to, so the only thing a wire function would add is the *database's* clock.

### The rest, by bucket

- **Bare-scalar and whole-entity projections.** A response is keyed by member name, so a projection [must construct an object](#anonymous-types); a whole-entity projection would return rows unshaped, which the projection contract exists to prevent.
- **Collection-valued projections** — `Select(_ => new { _.Posts })`, materialized `IGrouping`s, `CROSS APPLY` collections in the result, arbitrary nesting depth. All put a nested collection in a response, which is exactly what keeps collections [aggregable and not projectable](annotations.md#collections).
- **`Cast`, `DefaultIfEmpty`, `SelectMany` result selectors, projected `GroupJoin` groups** — [deliberately left out](#deliberately-left-out).
- **`SkipWhile`, `TakeWhile`, `Zip`, `Aggregate`, `SequenceEqual`, comparer overloads** — [not gaps](#not-gaps): EF recognizes them and then fails their relational translation.
- **`Include`, tracking, split queries, `ExecuteUpdate` / `ExecuteDelete`, `FromSql`, table-valued functions, temporal tables, and the `EF.Functions` catalogue** (`Like`, `PatIndex`, full-text, JSON, `DataLength`, vector search, statistical aggregates) — [out of scope](#out-of-scope): server-side surface, and a hostile client is exactly the wrong party to hand it to.
