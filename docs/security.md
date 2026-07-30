# Security model

Scry lets a client compose queries. The design assumption is that **the client is hostile**: the generated code, the LINQ, and the serialized request are all attacker-controlled. Every guarantee is enforced on the server, at runtime, against the real model assembly.


## Threat model

Assumed:

- An attacker can craft arbitrary JSON and POST it to the query endpoint.
- An attacker can read the generated client code, and can see any schema the [explorer](explorer.md) exposes.
- An attacker will try to name types and properties that were never generated for them.

Not assumed:

- That the client-side type system constrains anything. It is a developer-experience feature, not a control.


## Layers


### 1. Default-deny allow-list

A type is invisible unless it carries `[Queryable]`, `[QueryableView]`, `[QueryablePoco]`, or `[QueryableComplex]`. A property is invisible if it carries `[QueryIgnore]`, has no public instance getter, or is not a scalar or a navigation to another opted-in type.

Adding an entity to the `DbContext` does not expose it. Adding a property to an exposed entity does expose it — that is the one direction where the default is open, and it is why the surface should be reviewed alongside model changes.

**Collection navigations are the exception to that**: they are invisible even on an exposed type until the *member itself* carries `[QueryableCollection]`, so adding one to a model never widens a surface by accident. An exposed collection is **aggregable, not projectable** — a client can ask `Any`, `All`, `Count`, `Sum`, `Average`, `Min`, or `Max` about it, evaluated as a correlated subquery, but can never enumerate its rows. Every answer is a scalar, so no request can return an unbounded nested collection and the page bounds are unchanged. Inside the subquery the element type's own allow-list applies, a `[QueryIgnore]`d member stays hidden, and a subquery may not appear inside another subquery.

A collection whose element type carries a [row policy](policies.md) is **refused at startup**. A policy filters a source; a subquery has none, so aggregating a policied collection would count exactly the rows the policy exists to hide. The same caveat applies more narrowly to reference navigations, which are not policy-filtered when traversed — see [what Scry does not do](#what-scry-does-not-do).

**Inheritance is not transitive either.** An opt-in attribute is read off the type it is written on and never inherited, so a subclass of an exposed type is invisible until it opts in itself. Adding one to a model exposes neither its rows nor the members it declares, and no query can narrow to it: [`OfType`](querying.md#narrowing-to-a-derived-type) names the target as a wire string, which is resolved through the allow-list and then checked to actually derive from the type being queried — so it can only ever narrow, never widen back to a base or across to an unrelated source. A [row policy](policies.md) is the deliberate exception: it *is* inherited, so a subclass cannot shed the one its base carries, and where both carry one, both apply.

**Complex types and JSON columns** follow the same default-deny rule. A complex/value type — including one mapped into a JSON column — is invisible until it carries `[QueryableComplex]`, and even then only its allow-listed scalar leaves are reachable (`[QueryIgnore]` still hides members). Exposing a complex type does not transitively expose anything: a nested type it references is reachable only if it too is opted in, so a JSON column cannot smuggle in an entity or an unlisted field. Traversal into a complex member counts against `MaxNavigationDepth` exactly like a navigation, bounding how deeply a client can descend into nested JSON. How EF stores the type (JSON or columns) never changes what is reachable — the allow-list is built from the CLR/annotation surface, not the storage mapping.


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
[JsonDerivedType(typeof(ConstNode), "const")]
[JsonDerivedType(typeof(BinaryNode), "binary")]
[JsonDerivedType(typeof(UnaryNode), "unary")]
[JsonDerivedType(typeof(CallNode), "call")]
[JsonDerivedType(typeof(ConditionalNode), "conditional")]
[JsonDerivedType(typeof(SubqueryNode), "subquery")]
[JsonDerivedType(typeof(CollateNode), "collate")]
[JsonDerivedType(typeof(InSourceNode), "inSource")]
[JsonDerivedType(typeof(AggregateNode), "aggregate")]
public abstract record Node;
```
<sup><a href='/src/Scry.Wire/Expressions/Node.cs#L7-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireExpressions' title='Start of snippet'>anchor</a></sup>
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
    DateYear,
    DateMonth,
    DateDay,
    DateHour,
    DateMinute,
    DateSecond,
    DateMillisecond,
    DateDayOfYear,

    /// <summary>
    /// The day of the week, numbered as <see cref="System.DayOfWeek"/> does — 0 for Sunday. The server
    /// owns how that is expressed in SQL, since the obvious formulation is not deterministic.
    /// </summary>
    DateDayOfWeek,
    DateDate,
    DateAddYears,
    DateAddMonths,
    DateAddDays,
    DateAddHours,
    DateAddMinutes,
    DateAddSeconds,
    MathAbs,
    MathCeiling,
    MathFloor,
    MathRound,
    MathTruncate,
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
    /// Membership of a client-supplied set (SQL <c>IN</c>). The target is the value being tested and
    /// every argument is a <see cref="ConstNode"/>; the server caps the number of values.
    /// </summary>
    In
}
```
<sup><a href='/src/Scry.Wire/KnownFunction.cs#L3-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireFunctions' title='Start of snippet'>anchor</a></sup>
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

A [join](querying.md#joins) and a [membership test against another source](querying.md#membership-of-another-source) both resolve their second source through the same path: that source's policy is applied **before** the two sides meet, so a join can only narrow and never becomes a way to observe rows through a source whose policy hides them. The same reasoning is why a [collection navigation](annotations.md#collections) of a policied type is refused outright — there, the aggregate has no source for a policy to filter.

See [Row policies](policies.md).


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
/// Maximum number of rows a streamed query may return, or null — the default — for no limit.
/// </summary>
/// <remarks>
/// Null matches <c>ToListAsync</c>, which has never been bounded either: <see cref="MaxPageSize"/>
/// caps <c>Take</c> and a page, not an unbounded enumeration. Streaming is the safer of the two
/// server-side, since the rows are never buffered — but it holds a connection and a response open
/// for as long as the client reads, which is the reason to offer a bound at all. A stream that
/// reaches the limit ends with an error marker rather than a short result, so a client cannot
/// mistake truncation for the end of the data.
/// </remarks>
public int? MaxStreamRows { get; set; }
```
<sup><a href='/src/Scry.Server/ScryOptions.cs#L9-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-scryOptionsLimits' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

These bound the work a single request can ask for: how many rows, how deep a join chain, how long a pipeline, how deeply nested an expression.


### 7. Contained errors

Validation and wire failures return `400` with a specific message — the message names the rejected property or rule, which is not a disclosure beyond what the allow-list already implies. Everything else returns `500`.

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
                "left": { "$type": "member", "path": ["Salary"] },
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
<sup><a href='/IntegrationTests/HttpRoundTripTests.cs#L206-L233' title='Snippet source file'>snippet source</a> | <a href='#snippet-rawRequestRejected' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## What Scry does not do

**Authentication and authorization.** Scry has no notion of a user. Put it on the endpoint:

```cs
app.MapScry("/api/query")
    .RequireAuthorization("Reader");
```

**Plan-cache pressure from set membership.** Scalar constants are bound as parameters, but the values of a `Contains` set are still emitted into the statement, so a client varying the *size* of that set produces a distinct statement each time. `MaxInValues` bounds how large one gets; if plan-cache pressure matters for a deployment, lower it.

**Rate limiting and cost control.** The limits bound the *shape* of a query, not its cost. An allow-listed query over a large unindexed table is still expensive, and `MaxPageSize` caps an explicit `Take` rather than implicitly paging an unbounded query. Apply ASP.NET Core rate limiting, a command timeout, and the usual database-side controls.

**Column-level authorization per user.** `[QueryIgnore]` is static: a column is exposed or it is not. There is no per-caller column masking. Expose a view containing only the permitted columns instead.

**Row policies on traversed navigations.** A policy is applied to the *source* a query names, not to types reached from it. Traversing a reference navigation reads the target row directly — `_.Manager!.Name` returns a manager the `Employee` policy would have filtered out of a top-level query. Do not rely on a row policy to hide a row that is reachable as a navigation target from an exposed type; hide the member with `[QueryIgnore]`, or do not expose the navigation. Collection navigations avoid the wider version of this by refusing to expose a policied element type at all.

**Auditing.** Nothing is logged by default. `ScryProcessor.Execute` is the single choke point for recording what was asked for.

**CORS, CSRF, TLS.** Ordinary ASP.NET Core concerns, unchanged by Scry.


## Review checklist

- [ ] Every `[Queryable]` type is intended to be client-readable, and its exposed properties reviewed.
- [ ] Sensitive columns carry `[QueryIgnore]` — and any newly added ones too.
- [ ] Multi-tenant sources have a row policy.
- [ ] The query endpoint requires authentication/authorization.
- [ ] `MaxPageSize` matches what the UI actually needs.
- [ ] The [explorer](explorer.md) is either unmapped or behind a real guard in production.
- [ ] Rate limiting and a database command timeout are configured.
