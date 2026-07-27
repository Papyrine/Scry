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

A type is invisible unless it carries `[Queryable]`, `[QueryableView]`, `[QueryablePoco]`, or `[QueryableComplex]`. A property is invisible if it carries `[QueryIgnore]`, has no public instance getter, or is not a scalar or a navigation to another opted-in type. Collection navigations are never exposed.

Adding an entity to the `DbContext` does not expose it. Adding a property to an exposed entity does expose it — that is the one direction where the default is open, and it is why the surface should be reviewed alongside model changes.

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
[JsonDerivedType(typeof(GroupByOp), "groupBy")]
[JsonDerivedType(typeof(CountOp), "count")]
[JsonDerivedType(typeof(AnyOp), "any")]
[JsonDerivedType(typeof(FirstOp), "first")]
[JsonDerivedType(typeof(SingleOp), "single")]
[JsonDerivedType(typeof(PageOp), "page")]
public abstract record QueryOp;
```
<sup><a href='/src/Scry.Wire/Operators/QueryOp.cs#L8-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireOperators' title='Start of snippet'>anchor</a></sup>
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
[JsonDerivedType(typeof(AggregateNode), "aggregate")]
public abstract record Node;
```
<sup><a href='/src/Scry.Wire/Expressions/Node.cs#L7-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireExpressions' title='Start of snippet'>anchor</a></sup>
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
    DateYear,
    DateMonth,
    DateDay
}
```
<sup><a href='/src/Scry.Wire/Enums.cs#L38-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-wireFunctions' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='/src/Scry.Tests/SecurityTests.cs#L6-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-rejectIgnoredProperty' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### 4. Typed rebinding

CLR types are introduced only from the schema, never from the wire. Member access is built by looking the name up in the allow-list and using the `PropertyInfo` found there, so there is no path from a wire string to a reflected member that was not already allow-listed.

Constants are the one attacker-supplied value that reaches the query. They travel as a string plus a type tag and are parsed into the **member's** type at the comparison site — not into whatever type the tag claims. They become `Expression.Constant` nodes, which EF Core parameterizes; they are never concatenated into SQL.

### 5. Row policies

An `IReturnablePolicy<T>` is applied to the source before any client operator, so client filters can<!-- include: policy-ordering. path: /docs/includes/policy-ordering.include.md -->
only narrow an already-authorized set.<!-- endInclude -->

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
```
<sup><a href='/src/Scry.Server/ScryOptions.cs#L9-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-scryOptionsLimits' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

These bound the work a single request can ask for: how many rows, how deep a join chain, how long a pipeline, how deeply nested an expression.

### 7. Contained errors

Validation and wire failures return `400` with a specific message — the message names the rejected property or rule, which is not a disclosure beyond what the allow-list already implies. Everything else returns `500`.

The `500` body is fixed — `{"error":"Query execution failed."}` — and stack traces, SQL, and EF Core<!-- include: error-500-body. path: /docs/includes/error-500-body.include.md -->
messages are never returned to the client.<!-- endInclude -->

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
<sup><a href='/IntegrationTests/HttpRoundTripTests.cs#L117-L144' title='Snippet source file'>snippet source</a> | <a href='#snippet-rawRequestRejected' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## What Scry does not do

**Authentication and authorization.** Scry has no notion of a user. Put it on the endpoint:

```cs
app.MapScry("/api/query")
    .RequireAuthorization("Reader");
```

**Rate limiting and cost control.** The limits bound the *shape* of a query, not its cost. An allow-listed query over a large unindexed table is still expensive, and `MaxPageSize` caps an explicit `Take` rather than implicitly paging an unbounded query. Apply ASP.NET Core rate limiting, a command timeout, and the usual database-side controls.

**Column-level authorization per user.** `[QueryIgnore]` is static: a column is exposed or it is not. There is no per-caller column masking. Expose a view containing only the permitted columns instead.

**Auditing.** Nothing is logged by default. `ScryProcessor.Execute` is the single choke point if you want to record what was asked for.

**CORS, CSRF, TLS.** Ordinary ASP.NET Core concerns, unchanged by Scry.

## Review checklist

- [ ] Every `[Queryable]` type is intended to be client-readable, and its exposed properties reviewed.
- [ ] Sensitive columns carry `[QueryIgnore]` — and any newly added ones too.
- [ ] Multi-tenant sources have a row policy.
- [ ] The query endpoint requires authentication/authorization.
- [ ] `MaxPageSize` matches what the UI actually needs.
- [ ] The [explorer](explorer.md) is either unmapped or behind a real guard in production.
- [ ] Rate limiting and a database command timeout are configured.
