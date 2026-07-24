# Security model

Scry lets a client compose queries. The design assumption is that **the client is hostile**: the
generated code, the LINQ, and the serialized request are all attacker-controlled. Every guarantee is
enforced on the server, at runtime, against the real model assembly.

## Threat model

Assumed:

- An attacker can craft arbitrary JSON and POST it to the query endpoint.
- An attacker can read the generated client code, and can see any schema the
  [explorer](explorer.md) exposes.
- An attacker will try to name types and properties that were never generated for them.

Not assumed:

- That the client-side type system constrains anything. It is a developer-experience feature, not a
  control.

## Layers

### 1. Default-deny allow-list

A type is invisible unless it carries `[Queryable]`, `[QueryableView]`, or `[QueryablePoco]`. A
property is invisible if it carries `[QueryIgnore]`, has no public instance getter, or is not a
scalar or a navigation to another opted-in type. Collection navigations are never exposed.

Adding an entity to the `DbContext` does not expose it. Adding a property to an exposed entity does
expose it — that is the one direction where the default is open, and it is why the surface should be
reviewed alongside model changes.

### 2. A closed AST

The wire format has no node for an arbitrary method call, no node for raw SQL, and no node for a type
name. The full vocabulary is:

snippet: wireOperators

snippet: wireExpressions

snippet: wireFunctions

Unknown discriminators fail deserialization rather than being ignored, so a request that names
anything outside these sets is rejected at the JSON layer.

### 3. Server-side revalidation

The server rebuilds the allow-list at startup from the real model assembly, independently of whatever
the client was generated against. `QueryValidator` then walks every incoming AST and rejects:

- An unknown root source.
- A property that is not allow-listed on the type reached so far.
- Traversal through a non-navigation member (`Name.Length`).
- A wire version newer than the server understands.
- An ill-formed pipeline: `ThenBy` without `OrderBy`, an operator after a terminal, more than one
  `GroupBy` or `Select`, `Where`/`OrderBy` after `GroupBy` or `Select`, `GroupBy` without a following
  `Select`, a terminal predicate after a `Select`.
- An aggregate outside a grouped `Select`, or a grouped projection referencing a non-key member.
- An empty projection, or a projection leaf that is not a scalar.
- Any resource limit overrun.

Validation runs to completion **before** any expression is rebound or executed. A rejected query
never reaches EF Core.

snippet: rejectIgnoredProperty

### 4. Typed rebinding

CLR types are introduced only from the schema, never from the wire. Member access is built by looking
the name up in the allow-list and using the `PropertyInfo` found there, so there is no path from a
wire string to a reflected member that was not already allow-listed.

Constants are the one attacker-supplied value that reaches the query. They travel as a string plus a
type tag and are parsed into the **member's** type at the comparison site — not into whatever type
the tag claims. They become `Expression.Constant` nodes, which EF Core parameterizes; they are never
concatenated into SQL.

### 5. Row policies

An `IReturnablePolicy<T>` is applied to the source before any client operator, so client filters can
only narrow an already-authorized set. See [Row policies](policies.md).

### 6. Resource limits

snippet: scryOptionsLimits

These bound the work a single request can ask for: how many rows, how deep a join chain, how long a
pipeline, how deeply nested an expression.

### 7. Contained errors

Validation and wire failures return `400` with a specific message — the message names the rejected
property or rule, which is not a disclosure beyond what the allow-list already implies. Everything
else returns `500` with the fixed body `{"error":"Query execution failed."}`. Stack traces, SQL, and
EF Core messages are never returned.

## End to end

The generated client model has no `Salary` member, so a hostile client must forge the request by
hand. The server rejects it:

snippet: rawRequestRejected

## What Scry does not do

**Authentication and authorization.** Scry has no notion of a user. Put it on the endpoint:

```cs
app.MapScry("/api/query")
    .RequireAuthorization("Reader");
```

**Rate limiting and cost control.** The limits bound the *shape* of a query, not its cost. An
allow-listed query over a large unindexed table is still expensive, and `MaxPageSize` caps an explicit
`Take` rather than implicitly paging an unbounded query. Apply ASP.NET Core rate limiting, a
command timeout, and the usual database-side controls.

**Column-level authorization per user.** `[QueryIgnore]` is static: a column is exposed or it is not.
There is no per-caller column masking. Expose a view containing only the permitted columns instead.

**Auditing.** Nothing is logged by default. `ScryProcessor.Execute` is the single choke point if you
want to record what was asked for.

**CORS, CSRF, TLS.** Ordinary ASP.NET Core concerns, unchanged by Scry.

## Review checklist

- [ ] Every `[Queryable]` type is intended to be client-readable, and its exposed properties reviewed.
- [ ] Sensitive columns carry `[QueryIgnore]` — and any newly added ones too.
- [ ] Multi-tenant sources have a row policy.
- [ ] The query endpoint requires authentication/authorization.
- [ ] `MaxPageSize` matches what the UI actually needs.
- [ ] The [explorer](explorer.md) is either unmapped or behind a real guard in production.
- [ ] Rate limiting and a database command timeout are configured.
