# Security model

Scry lets a client compose queries. The design assumption is that **the client is hostile**: the generated code, the LINQ, and the serialized request are all attacker-controlled. Every guarantee is enforced on the server, at runtime, against the real model assembly.


## Threat model

Assumed:

- An attacker can craft arbitrary JSON and send it to the query endpoint, in a `POST` body or [encoded into a `GET` URL](wire-format.md#the-url-form). Both reach the same handler and are validated identically; neither form is trusted more than the other, and a URL that does not decode into a request this server can parse is rejected before anything is bound.
- An attacker can read the generated client code, and can see any schema the [explorer](explorer.md) exposes.
- An attacker will try to name types and properties that were never generated for them.

Sensitivity is a separate axis from access. [`[Sensitive]`](annotations.md#sensitive) does not decide *whether* a member may be read — the allow-list and [row policies](policies.md) do that — it decides how the value may travel and whether the answer may be kept. A caller allowed to read a member is allowed to read it either way; what changes is that the value never reaches an access log and the response is never stored.

Not assumed:

- That the client-side type system constrains anything. It is a developer-experience feature, not a control.


## Layers


### 1. Default-deny allow-list

A type is invisible unless it carries `[Queryable]`, `[QueryableView]`, `[QueryablePoco]`, or `[QueryableComplex]`. A property is invisible if it carries `[QueryIgnore]`, has no public instance getter, or is not a scalar, a navigation to another opted-in type, or an opted-in collection.

Adding an entity to the `DbContext` does not expose it. Adding a property to an exposed entity does expose it — that is the one direction where the default is open, and it is why the surface should be reviewed alongside model changes.

**Collection navigations are the exception to that**: they are invisible even on an exposed type until the *member itself* carries `[QueryableCollection]`, so adding one to a model never widens a surface by accident. An exposed collection is **aggregable, not projectable** — a client can ask `Any`, `All`, `Count`, `Sum`, `Average`, `Min`, or `Max` about it, evaluated as a correlated subquery, but can never enumerate its rows. Every answer is a scalar, so no request can return an unbounded nested collection and the page bounds are unchanged. Inside the subquery the element type's own allow-list applies, a `[QueryIgnore]`d member stays hidden, and a subquery may not appear inside another subquery.

A collection whose element type carries a [row policy](policies.md) is **refused at startup**. A policy filters a source; a subquery has none, so aggregating a policied collection would count exactly the rows the policy exists to hide. A reference navigation into a policied type is not refused but [rewritten](policies.md#reached-through-a-navigation): it is read through that type's policy, so a hidden row reads as null. A `[QueryableComplex]` type carrying a policy is refused for the same reason and at the same point: it has no source of its own for one to filter, so the policy could never run.

A collection of **values** — an EF primitive collection, typically a JSON column — is exposed by the same opt-in and answers the same aggregates, with two differences that follow from an element being a value rather than a row. Its element is read with the wire's `element` node, which the validator accepts **only** where the row being read is a scalar, so it can never be used to name a whole entity; and it cannot be flattened, since the rows a flatten would produce have no members for the operators after it to name.

**Inheritance is not transitive either.** An opt-in attribute is read off the type it is written on and never inherited, so a subclass of an exposed type is invisible until it opts in itself. Adding one to a model exposes neither its rows nor the members it declares, and no query can narrow to it: [`OfType`](querying.md#narrowing-to-a-derived-type) names the target as a wire string, which is resolved through the allow-list and then checked to actually derive from the type being queried — so it can only ever narrow, never widen back to a base or across to an unrelated source. A [row policy](policies.md) is the deliberate exception: it *is* inherited, so a subclass cannot shed the one its base carries, and where both carry one, both apply. That inheritance runs [downwards only](policies.md#inheritance-runs-downwards-only) — a policy on a derived type does not filter the base source, which returns and counts those rows as instances of the base (exposing the base's members, never the derived type's). Restricting a hierarchy however it is reached means attaching the policy to the base.

**Complex types and JSON columns** follow the same default-deny rule. A complex/value type — including one mapped into a JSON column — is invisible until it carries `[QueryableComplex]`, and even then only its allow-listed scalar leaves are reachable (`[QueryIgnore]` still hides members). A JSON *array* — a collection of such a type, or a collection of values — needs the member's own `[QueryableCollection]` on top of that, and is aggregable exactly like any other collection. Exposing a complex type does not transitively expose anything: a nested type it references is reachable only if it too is opted in, so a JSON column cannot smuggle in an entity or an unlisted field, and a `[QueryIgnore]`d member stays unreadable inside an array even though EF still writes it there. Traversal into a complex member counts against `MaxNavigationDepth` exactly like a navigation, bounding how deeply a client can descend into nested JSON. How EF stores the type (JSON or columns) never changes what is reachable — the allow-list is built from the CLR/annotation surface, not the storage mapping.


### 2. A closed AST

The wire format has no node for an arbitrary method call, no node for raw SQL, and no node for a type name. The full vocabulary is:

<!-- snippet: wireOperators -->
<a id='snippet-wireOperators'></a>
```cs
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WhereOp), "where")]
[JsonDerivedType(typeof(OrderByOp), "orderBy")]
[JsonDerivedType(typeof(ThenByOp), "thenBy")]
[JsonDerivedType(typeof(SkipOp), "skip")]
[JsonDerivedType(typeof(TakeOp), "take")]
[JsonDerivedType(typeof(SelectOp), "select")]
[JsonDerivedType(typeof(SelectManyOp), "selectMany")]
[JsonDerivedType(typeof(OfTypeOp), "ofType")]
[JsonDerivedType(typeof(GroupByOp), "groupBy")]
[JsonDerivedType(typeof(DistinctOp), "distinct")]
[JsonDerivedType(typeof(ReverseOp), "reverse")]
[JsonDerivedType(typeof(JoinOp), "join")]
[JsonDerivedType(typeof(SetOp), "set")]
[JsonDerivedType(typeof(CountOp), "count")]
[JsonDerivedType(typeof(LongCountOp), "longCount")]
[JsonDerivedType(typeof(AnyOp), "any")]
[JsonDerivedType(typeof(AllOp), "all")]
[JsonDerivedType(typeof(FirstOp), "first")]
[JsonDerivedType(typeof(SingleOp), "single")]
[JsonDerivedType(typeof(LastOp), "last")]
[JsonDerivedType(typeof(AggregateOp), "aggregate")]
[JsonDerivedType(typeof(PageOp), "page")]
public abstract record QueryOp;
```
<sup><a href='/src/Scry.Wire/Operators/QueryOp.cs#L8-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireOperators' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: wireExpressions -->
<a id='snippet-wireExpressions'></a>
```cs
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MemberNode), "member")]
[JsonDerivedType(typeof(ElementNode), "element")]
[JsonDerivedType(typeof(ConstNode), "const")]
[JsonDerivedType(typeof(BinaryNode), "binary")]
[JsonDerivedType(typeof(UnaryNode), "unary")]
[JsonDerivedType(typeof(CallNode), "call")]
[JsonDerivedType(typeof(ConditionalNode), "conditional")]
[JsonDerivedType(typeof(SubqueryNode), "subquery")]
[JsonDerivedType(typeof(CollateNode), "collate")]
[JsonDerivedType(typeof(InSourceNode), "inSource")]
[JsonDerivedType(typeof(AggregateNode), "aggregate")]
[JsonDerivedType(typeof(GroupKeyNode), "groupKey")]
[JsonDerivedType(typeof(CompositeKeyNode), "compositeKey")]
public abstract record Node;
```
<sup><a href='/src/Scry.Wire/Expressions/Node.cs#L7-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireExpressions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: wireFunctions -->
<a id='snippet-wireFunctions'></a>
```cs
/// <summary>The closed set of functions a client may call on a value. No free-form method names.</summary>
public enum KnownFunction
{
    StringContains,
    StringStartsWith,
    StringEndsWith,
    StringToLower,
    StringToUpper,
    StringIsNullOrEmpty,
    StringIsNullOrWhiteSpace,
    StringLength,
    StringTrim,
    StringTrimStart,
    StringTrimEnd,
    StringSubstring,
    StringIndexOf,
    StringReplace,

    /// <summary>
    /// The first and last character of a string, as <c>FirstOrDefault</c> and <c>LastOrDefault</c>
    /// spell them — a substring of one, taken at either end. The indexer that looks like it means the
    /// same is not carried: no provider translates it, and one that reads past the end of the text
    /// would fault where these answer with the default.
    /// </summary>
    StringFirst,
    StringLast,
    DateYear,
    DateMonth,
    DateDay,
    DateHour,
    DateMinute,
    DateSecond,
    DateMillisecond,
    DateDayOfYear,

    /// <summary>
    /// The sub-millisecond parts, each within the one above it: 0-999 microseconds of the
    /// millisecond, 0-999 nanoseconds of the microsecond. SQL Server's DATEPART counts them from the
    /// whole second, so the server takes the remainder, exactly as EF does.
    /// </summary>
    DateMicrosecond,
    DateNanosecond,

    /// <summary>The count of days since 0001-01-01 (<c>DateOnly.DayNumber</c>).</summary>
    DateDayNumber,

    /// <summary>
    /// The day of the week, numbered as <see cref="System.DayOfWeek"/> does — 0 for Sunday. The server
    /// owns how that is expressed in SQL, since the obvious formulation is not deterministic.
    /// </summary>
    DateDayOfWeek,
    DateDate,

    /// <summary>
    /// The time of day a date carries, as the <see cref="System.TimeSpan"/> since midnight. The
    /// counterpart of <see cref="DateDate"/>, which drops the same part instead of keeping it.
    /// </summary>
    DateTimeOfDay,

    /// <summary>
    /// The parts of an elapsed time, each within the unit above it — the hours of the day, the
    /// minutes of the hour, and so on down. Whole totals (<c>TotalHours</c> and its siblings) are a
    /// division rather than a part and no provider translates them, so they are not carried.
    /// </summary>
    TimeSpanHours,
    TimeSpanMinutes,
    TimeSpanSeconds,
    TimeSpanMilliseconds,
    TimeSpanMicroseconds,
    TimeSpanNanoseconds,

    /// <summary>
    /// Reading one temporal type as another: the date or the time half of a timestamp, a time read as
    /// an elapsed time, and a date and a time composed back into one. Each is a conversion the
    /// database performs, so the answer does not depend on the client's calendar or its clock.
    /// </summary>
    DateOnlyFromDateTime,
    TimeOnlyFromDateTime,
    TimeOnlyFromTimeSpan,
    DateTimeFromDateAndTime,

    /// <summary>
    /// Unix time, counted from 1970-01-01 UTC (<c>DateTimeOffset.ToUnixTimeSeconds</c>). The
    /// <c>DateTime</c> / <c>UtcDateTime</c> / <c>LocalDateTime</c> readings of an offset are not
    /// carried alongside them: the provider has a translation only for a column whose store type is
    /// <c>datetimeoffset</c> and refuses the expression otherwise, and the local reading would go
    /// through <c>CURRENT_TIMEZONE_ID()</c> — the server's own zone — even where it does translate.
    /// </summary>
    UnixSecondsFromOffset,
    UnixMillisecondsFromOffset,

    DateAddYears,
    DateAddMonths,
    DateAddDays,
    DateAddHours,
    DateAddMinutes,
    DateAddSeconds,
    DateAddMilliseconds,
    /// <summary>
    /// Joins the target and the argument into one string, converting either if it is not one already.
    /// C# writes this as <c>+</c>, but the operator alone does not say it: an Add of a string and a
    /// number is a concatenation, while an Add of two numbers is arithmetic, and only the client can
    /// tell which was written.
    /// </summary>
    StringConcat,

    /// <summary>
    /// The target's value as text — <c>ToString()</c> with no arguments. The formatted overload is not
    /// part of the set: no provider translates it, and the SQL function that would express it reads
    /// the server's language, so the same row would format differently per connection.
    /// </summary>
    StringFrom,

    MathAbs,
    MathCeiling,
    MathFloor,
    MathRound,
    MathTruncate,
    /// <summary>
    /// The sign of the target: -1, 0, or 1. The server composes it from comparisons rather than from
    /// SQL's own function, whose result takes the argument's type and so cannot be read back as the
    /// <see cref="int"/> this returns.
    /// </summary>
    MathSign,

    MathSqrt,
    MathPow,
    MathExp,

    /// <summary>Natural logarithm, or — with one argument — the logarithm to that base.</summary>
    MathLog,
    MathLog10,
    MathSin,
    MathCos,
    MathTan,
    MathAsin,
    MathAcos,
    MathAtan,

    /// <summary>The angle whose tangent is the target over the argument (<c>Math.Atan2(y, x)</c>).</summary>
    MathAtan2,

    /// <summary>
    /// The greater / lesser of the target and the argument (<c>Math.Max</c> / <c>Math.Min</c>). The
    /// server composes each from a comparison rather than using SQL's GREATEST and LEAST, which exist
    /// only from SQL Server 2022; a null operand keeps the answer null.
    /// </summary>
    MathMax,
    MathMin,

    /// <summary>
    /// Degrees to radians and back (<c>double.DegreesToRadians</c> / <c>RadiansToDegrees</c> —
    /// statics on the floating types rather than on <c>Math</c>). Defined over double alone, so the
    /// target is widened to reach them.
    /// </summary>
    MathDegreesToRadians,
    MathRadiansToDegrees,

    /// <summary>
    /// Membership of a client-supplied set (SQL <c>IN</c>). The target is the value being tested and
    /// every argument is a <see cref="ConstNode"/>; the server caps the number of values.
    /// </summary>
    In,

    /// <summary>
    /// Whether the target — a [Flags] enum member — carries the argument's bits
    /// (<c>Enum.HasFlag</c>). A combined flag travels by name exactly as <c>Enum.ToString</c> spells
    /// it: <c>"Parking, Gym"</c>.
    /// </summary>
    EnumHasFlag,

    /// <summary>
    /// Reads text as a value — <c>int.Parse</c> / <c>Convert.ToInt32</c> and their siblings; the
    /// inverse of <see cref="StringFrom"/>. Only that direction exists: a numeric member is already a
    /// value, and SQL's numeric-to-numeric conversions truncate where the CLR's round, so those are
    /// not carried. Text that does not parse faults at execution, exactly as it would in memory.
    /// </summary>
    Int32From,
    Int64From,
    DecimalFrom,
    DoubleFrom,
    BooleanFrom,
    ByteFrom,
    Int16From,
    SingleFrom,

    /// <summary>
    /// Three-way comparison (<c>a.CompareTo(b)</c>, <c>string.Compare(a, b)</c>): -1, 0, or 1, or
    /// null when either operand is — a comparison against a value that is not there has no direction.
    /// Numbers, text and dates compare; text compares under the server's collation, exactly as its
    /// ordering does.
    /// </summary>
    CompareTo,

    /// <summary>
    /// Questions about a binary member's bytes, without reading them: how many there are
    /// (<c>DATALENGTH</c>), whether a byte is among them (<c>CHARINDEX</c>), and the byte at one
    /// position. An <c>[Attachment]</c> answers none of them — its value is the one thing no query
    /// reads — so these reach a plain or <c>[BinaryTransfer]</c> member only. <c>Any()</c> is absent
    /// because the provider refuses it; ask whether <see cref="BytesLength"/> is above zero, which is
    /// the same question and does translate.
    /// </summary>
    BytesLength,
    BytesContains,
    BytesElementAt
}
```
<sup><a href='/src/Scry.Wire/KnownFunction.cs#L3-L210' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireFunctions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Unknown discriminators fail deserialization rather than being ignored, so a request that names anything outside these sets is rejected at the JSON layer.


### 3. Server-side revalidation

The server rebuilds the allow-list at startup from the real model assembly, independently of whatever the client was generated against. `QueryValidator` then walks every incoming AST and rejects:

- An unknown root source.
- A property that is not allow-listed on the type reached so far.
- Traversal through a non-navigation member (`Name.Length`).
- A wire version newer than the server understands.
- An ill-formed pipeline: `ThenBy` without `OrderBy`, an operator after a terminal, more than one `GroupBy` or `Select`, `Where`/`OrderBy` after `GroupBy` or `Select`, `GroupBy` without a following `Select`, a terminal predicate after a `Select`.
- An aggregate outside a grouped `Select`, or a grouped projection referencing a non-key member.
- An empty projection, or a projection leaf that is not a scalar.
- Any resource limit overrun.

Validation runs to completion **before** any expression is rebound or executed. A rejected query never reaches EF Core.

The optional [schema stamp](wire-format.md#schema-stamp) on a request is **not** a security input. It is attacker-controlled like the rest of the wire, is never consulted while deciding whether a query is allowed, and cannot widen the allow-list or unlock a source. It is read only *after* a query has already been rejected, to add a "the client looks stale" note to the error message. Forging or omitting it changes nothing about what a client may query.

<!-- snippet: rejectIgnoredProperty -->
<a id='snippet-rejectIgnoredProperty'></a>
```cs
[Test]
public void RejectsIgnoredProperty() =>
    AssertRejected(QueryRequest.Create(
        "Employee",
        [
            new WhereOp(new BinaryNode(
                BinaryOp.GreaterThan,
                new MemberNode(["Salary"]),
                new ConstNode("100", ClrTypeTag.Decimal)))
        ]));
```
<sup><a href='/src/Scry.Tests/SecurityTests.cs#L4-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-rejectIgnoredProperty' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### 4. Typed rebinding

CLR types are introduced only from the schema, never from the wire. Member access is built by looking the name up in the allow-list and using the `PropertyInfo` found there, so there is no path from a wire string to a reflected member that was not already allow-listed.

**Collations are the exception that proves the rule.** A collation cannot be a query parameter — a provider emits it into the SQL text — so a request never carries one. A client asks only for a [case sensitivity](querying.md#operators-1); which collation implements it is server configuration, and a server that has configured none rejects the request. That keeps the invariant below intact: no attacker-supplied string reaches SQL as anything but a parameter.

Constants are the one attacker-supplied value that reaches the query. They travel as a string plus a type tag and are parsed into the **member's** type at the comparison site — not into whatever type the tag claims.

A parsed value is then emitted the way a captured variable reaches a query — a member read off a holder object — which is the shape EF Core's funcletizer lifts into a **query parameter**. The value is bound, never written into the statement text.

That shape is deliberate and worth keeping. A bare `Expression.Constant` is *not* parameterized: EF inlines it into the SQL, escaped by the provider's type mapping. Escaping makes that safe from injection, but it makes the statement text differ per value, so every distinct value a client sends compiles and caches a plan of its own — a cheap way for a hostile client to flood the plan cache. Binding gives one plan for every value. A null is the exception and stays a literal: there is one of it, so nothing is gained, and a literal null keeps EF's `IS NULL` rewriting straightforward.


### 5. Row policies

An `IReturnablePolicy<T>` is applied to the source before any client operator, so client filters can<!-- include: policy-ordering. path: /docs/includes/policy-ordering.include.md -->
only narrow an already-authorized set.<!-- endInclude -->

A [join](querying.md#joins) and a [membership test against another source](querying.md#membership-of-another-source) both resolve their second source through the same path: that source's policy is applied **before** the two sides meet, so a join can only narrow and never becomes a way to observe rows through a source whose policy hides them.

A [reference navigation](policies.md#reached-through-a-navigation) into a policied type is the third route to the same rows, and is closed the same way: the traversal is rebound to read through the target's policy, so a hidden row reads as null wherever the path appears — a projection leaf, an ordering, a key, and above all a predicate, which runs in SQL and would otherwise answer about rows a direct query could never return. Because every rooted member path is rebound through one place, this holds for all of them rather than per operator. Startup translates each such traversal once and refuses to start if a policy does not compose there.

A [collection navigation](annotations.md#collections) of a policied type is the fourth, and is **refused at startup** unless the policy says how it wants to be read through: an aggregate off the owner has no source for a policy to filter, so it would count exactly the rows the policy hides. Opting into `DeniedCollectionMode.Hide` rewrites the collection into the same policy-filtered subquery a reference navigation uses, which makes an aggregate over it count what a direct query of the element source would have reached, and a flatten reach exactly those rows.

See [Row policies](policies.md).

#### Reporting a denial discloses that there was one

A denied row is hidden by default, at every one of those positions, and hiding is the only answer that discloses nothing. A policy can be configured to [fail the request](policies.md#what-a-denied-row-produces) instead, per position — a 403 carrying a fixed message that names no source, member, row, or policy.

That is a **deliberate existence oracle**. A caller that receives it learns that rows it may not see matched its query, which is exactly the signal hiding exists to withhold; by varying the query it can narrow down what those rows are. Enable it only where "you lack permission" is itself not sensitive — an internal tool, an auditable tenant — and never on a source whose row existence is the secret. A row another policy already hid is never reported, so raising one policy's mode cannot expose what a different one is hiding, and the outcome is audited as `Denied` so the disclosure is countable.

#### Cached decisions are server-held state

A [cached row policy](policies.md#when-the-decision-is-too-expensive-for-sql) moves the decision off the query and remembers it, keyed by policy, scope, and row key. Three things follow. The scope key must come from the authenticated principal via `context.Services` and never from a request header — it selects which set of answers applies, so a caller choosing it is a caller choosing its permissions. A row that is new or has changed is decided on its first read, so nothing is served on the strength of an answer nobody made. And a permission change reaches queries only when the host says it has, so the decisions can be stale by design: the lag is bounded by how promptly `InvalidateRows`/`InvalidateScope` are called, which makes calling them part of the authorization path rather than a cache optimization.

Decisions are made over the raw source rather than through its other policies, so one caller's view is never baked into an answer the others read; the other policies still apply to the query itself.

An [`IAttachmentPolicy<T>`](attachments.md#security) is the same idea for a value no query carries. A source exposing an `[Attachment]` refuses to start without one — the fetch endpoint is reached by row key rather than by a composed query, so the allow-list that stands between a caller and everything else has nothing to say about it. The row is still resolved through its source's row policies, so both apply, and a refusal is indistinguishable from a row that was never there.


### 6. Resource limits

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

/// <summary>
/// Maximum number of values a client may supply to a set-membership test (<c>Contains</c>, which
/// becomes a SQL <c>IN</c>). Default 1000.
/// </summary>
public int MaxInValues { get; set; } = 1000;

/// <summary>
/// Maximum number of queries one batch request may carry. Default 20.
/// </summary>
/// <remarks>
/// A batch is the one place a single request costs more than one query, so this is the bound that
/// keeps it from being an amplifier: every other limit is per query and would otherwise apply to an
/// arbitrary number of them. A batch over the limit is rejected whole, before any entry runs.
/// </remarks>
public int MaxBatchSize { get; set; } = 20;

/// <summary>
/// Maximum number of rows a streamed query may return, or null — the default — for no limit.
/// </summary>
/// <remarks>
/// Null matches <c>ToListAsync</c>, which has never been bounded either: <see cref="MaxPageSize"/>
/// caps <c>Take</c> and a page, not an unbounded enumeration. Nor is streaming the safer of the two
/// server-side any longer — a list that outgrows <see cref="ResponseSpillThreshold"/> is written out
/// as it is read, so neither holds its rows. What both hold is a connection and a response open for
/// as long as the client reads, which is the reason to offer a bound at all. A stream that
/// reaches the limit ends with an error marker rather than a short result, so a client cannot
/// mistake truncation for the end of the data.
/// </remarks>
public int? MaxStreamRows { get; set; }

/// <summary>
/// The longest encoded query this deployment wants asked as a URL. Default 4096; zero maps no GET
/// route at all, so every query travels as a body.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the limits above this one rejects nothing — it is advertised rather than enforced,
/// because the ceiling it describes is not this server's. What actually truncates or refuses a long
/// URL is whichever hop is strictest: 8 KB on a whole request line is the common default for a
/// server or a proxy, and the number here is the budget a client is asked to stay inside of so it
/// never finds out where the real edge is. A request that arrives is answered whatever its length.
/// </para>
/// <para>
/// It is a deployment setting rather than something the model declares, since the ingress in front
/// of a server is a property of where it runs — one model can be hosted behind two of them.
/// Clients learn it from <see cref="WireFormat.UrlLimitHeader"/>, carried on every response.
/// </para>
/// <para>
/// Zero is the exception, and is enforced: it says a query may never appear in a URL here, which is
/// a statement about this deployment rather than a guess about a length. <c>MapScry</c> honours it
/// by not mapping the GET route, so routing answers such a request with a 405 naming POST and Scry
/// never sees it. Setting it means giving up conditional requests — see /docs/caching.md.
/// </para>
/// </remarks>
public int QueryUrlLimit { get; set; } = QueryUrl.MaxLength;
```
<sup><a href='/src/Scry.Server/ScryOptions.cs#L9-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-scryOptionsLimits' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

These bound the work a single request can ask for: how many rows, how deep a join chain, how long a pipeline, how deeply nested an expression.

All but one are **per query**, which is what makes `MaxBatchSize` load-bearing: a [batch](batching.md) is the only request that carries more than one query, so without it every other limit would apply to an arbitrary number of them at once. Each entry is otherwise validated, policy-filtered, and audited exactly as it would be sent alone — batching is a transport concern, and reaches nothing else on this page.

[`ResponseSpillThreshold`](server.md#response-size) is deliberately not among them. It decides when a response stops being resident, not how large one may be: crossing it rejects nothing, and a request that would produce a gigabyte still produces a gigabyte. What it changes is where those bytes sit while they are produced.


### 7. Contained errors

Validation and wire failures return `400` with a specific message — the message names the rejected property or rule, which is not a disclosure beyond what the allow-list already implies. A constant that fails to parse into its member's type is a validation failure too: parsing happens while the expression is rebound, after validation has passed, but the request is still rejected with a message naming the value rather than surfacing as a server fault. Everything else returns `500`.

The `500` message is fixed — `Query execution failed.` — and stack traces, SQL, and EF Core<!-- include: error-500-body. path: /docs/includes/error-500-body.include.md -->
messages are never returned to the client. The only variable part is the `staleClient` marker.<!-- endInclude -->


## End to end

The generated client model has no `Salary` member, so a hostile client must forge the request by hand. The server rejects it:

<!-- snippet: rawRequestRejected -->
<a id='snippet-rawRequestRejected'></a>
```cs
[Test]
public async Task DisallowedPropertyRejectedWith400()
{
    const string json = """
        {
          "version": 1,
          "root": "Employee",
          "pipeline": [
            {
              "$type": "where",
              "predicate": {
                "$type": "binary",
                "op": "GreaterThan",
                "left": { "$type": "member", "path": "Salary" },
                "right": { "$type": "const", "value": "100", "tag": "Decimal" }
              }
            }
          ]
        }
        """;

    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    using var response = await http.PostAsync("/api/query", content);

    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
}
```
<sup><a href='/IntegrationTests/HttpRoundTripTests.cs#L342-L369' title='Snippet source file'>snippet source</a> | <a href='#snippet-rawRequestRejected' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## What Scry does not do

**Authentication and authorization.** Scry has no notion of a user. Put it on the endpoint:

```cs
app.MapScry("/api/query")
    .RequireAuthorization("Reader");
```

**Rate limiting and cost control.** The limits bound the *shape* of a query, not its cost. An allow-listed query over a large unindexed table is still expensive, and `MaxPageSize` caps an explicit `Take` rather than implicitly paging an unbounded query. Apply ASP.NET Core rate limiting, a command timeout, and the usual database-side controls.

**Bound how long a slow reader can hold a connection.** A response past [`ResponseSpillThreshold`](server.md#response-size) is written as it is read, so it holds a connection *and* its database read open for as long as the client takes to read it. That exposure is not new — `…/stream` has always had it, and `MapScry` maps every endpoint together precisely so the surface is uniform rather than one endpoint being protected while its neighbours are not — but it now reaches `ToListAsync` as well, which `MaxStreamRows` does not bound. Set the threshold to zero to hold responses whole as they once were, at the cost of an unbounded result being resident. The improvement in the same change is that such a result is no longer resident *twice*, as rows and as serialized bytes.

**Column-level authorization per user.** `[QueryIgnore]` is static: a column is exposed or it is not. There is no per-caller column masking. Expose a view containing only the permitted columns instead.

**Trusting a request header.** A row policy can read the call's headers off `ScryPolicyContext.RequestHeaders`, and the client can attach them [per query](querying.md#headers). Every one of them is chosen by the client and therefore attacker-controlled — a policy that scopes rows by `X-Tenant` scopes nothing, because an attacker sends a different `X-Tenant`. They are hint data: correlation ids, trace ids, a client build. Identity and tenancy come from the authenticated principal, resolved through `context.Services`.

**Auditing, by default.** Nothing is recorded until something subscribes. The hooks exist — every query is reported to any registered [`IScryAuditor`](observability.md#the-audit-hook) with its full request AST and outcome, alongside [traces and metrics](observability.md) — but turning them on, and alerting on rejections, is deployment work.

**CORS, CSRF, TLS.** Ordinary ASP.NET Core concerns, unchanged by Scry.

**Cache or range-serve an [attachment](attachments.md).** Every fetch is authorized afresh, and there is no `ETag`, `Cache-Control`, or `Range` support — a cached attachment is one the policy no longer sees. Add caching in middleware only where that trade is acceptable.

**Widen anything for [binary transfer](wire-format.md#binary-transfer).** `[BinaryTransfer]` changes how an already-allow-listed `byte[]` value is *encoded in the response* — a raw multipart part instead of base64 — and nothing about what a request may ask for: it adds no request-side input at all, and validation, policies, and limits are untouched. The response side is server-to-client and outside the hostile-client model; the client still bounds what it reads (the multipart reader, from the HttpMultipart package, keeps its header count/length limits), so a compromised or misbehaving server cannot make it buffer unbounded headers.


## Review checklist

- [ ] Every `[Queryable]` type is intended to be client-readable, and its exposed properties reviewed.
- [ ] Sensitive columns carry `[QueryIgnore]` — and any newly added ones too.
- [ ] Multi-tenant sources have a row policy.
- [ ] A policy meant to cover a hierarchy is attached to the base, not only to a subclass — it does not filter upwards.
- [ ] Every `[Attachment]` has a policy that authorizes the caller, not merely one that returns true.
- [ ] No row policy scopes rows by a request header — those are client-chosen. Nor does a cached policy's `ScopeKey`, which selects a whole set of decisions.
- [ ] Every position set to `DeniedRowMode.Error` is one where revealing that hidden rows exist is acceptable — hiding is the non-disclosing default.
- [ ] Every `[QueryableCollection]` opted into `DeniedCollectionMode.Hide` is one whose aggregates are meant to be answerable at all.
- [ ] Every grant change that a cached row policy would decide differently calls `InvalidateRows` or `InvalidateScope` — nothing else can know.
- [ ] Where conditional requests are on, whatever a cached policy's decisions depend on is in `CacheScope` — invalidating the policy does not move an `ETag`, so a caller holding one is answered `304` with the rows it no longer has access to.
- [ ] The query endpoint requires authentication/authorization.
- [ ] `MaxPageSize` matches what the UI actually needs.
- [ ] The [explorer](explorer.md) is either unmapped or behind a real guard in production.
- [ ] If the explorer is exposed to anyone in production, its [SQL preview](explorer.md#sql-preview) is left off — the SQL discloses real table and column names and the shape of every row policy.
- [ ] Rate limiting and a database command timeout are configured.
