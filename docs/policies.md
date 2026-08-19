# Row policies

A row policy narrows a source to the rows the caller is allowed to see, **before** any client operator is applied. It is the mechanism for tenant scoping, soft delete, and row-level security.

<!-- snippet: returnablePolicyInterface -->
<a id='snippet-returnablePolicyInterface'></a>
```cs
public interface IReturnablePolicy<T>
{
    IQueryable<T> Filter(IQueryable<T> source, ScryPolicyContext context);
}
```
<sup><a href='/src/Scry.Server/IReturnablePolicy.cs#L9-L14' title='Snippet source file'>snippet source</a> | <a href='#snippet-returnablePolicyInterface' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Writing a policy

<!-- snippet: returnablePolicy -->
<a id='snippet-returnablePolicy'></a>
```cs
/// <summary>A row policy that scopes <see cref="Employee"/> queries to active rows only.</summary>
public sealed class ActiveOnlyPolicy :
    IReturnablePolicy<Employee>
{
    public IQueryable<Employee> Filter(IQueryable<Employee> source, ScryPolicyContext context) =>
        source.Where(_ => _.Active);
}
```
<sup><a href='/src/Scry.Tests/TestModel.cs#L378-L386' title='Snippet source file'>snippet source</a> | <a href='#snippet-returnablePolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `context` carries the request-scoped service provider, the active `DbContext`, and the call's HTTP headers:

<!-- snippet: policyContext -->
<a id='snippet-policyContext'></a>
```cs
public sealed class ScryPolicyContext(
    IServiceProvider services,
    DbContext db,
    IHeaderDictionary requestHeaders,
    IHeaderDictionary responseHeaders)
{
    /// <summary>Context for a processor hosted outside the HTTP endpoint, which has no headers.</summary>
    public ScryPolicyContext(IServiceProvider services, DbContext db) :
        this(services, db, new HeaderDictionary(), new HeaderDictionary())
    {
    }

    /// <summary>The request-scoped service provider (e.g. for the current user/tenant).</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The active <see cref="DbContext"/>.</summary>
    public DbContext Db { get; } = db;

    /// <summary>
    /// The headers the caller sent. Client-supplied and therefore untrusted — hint data, never an
    /// authorization input.
    /// </summary>
    public IHeaderDictionary RequestHeaders { get; } = requestHeaders;

    /// <summary>The headers of the response being built. Writes here reach the client.</summary>
    public IHeaderDictionary ResponseHeaders { get; } = responseHeaders;
}
```
<sup><a href='/src/Scry.Server/ScryPolicyContext.cs#L7-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-policyContext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

so a realistic policy resolves whatever it needs from DI:

```cs
public sealed class TenantPolicy :
    IReturnablePolicy<Order>
{
    public IQueryable<Order> Filter(IQueryable<Order> source, ScryPolicyContext context)
    {
        var tenant = context.Services.GetRequiredService<ITenantAccessor>().Current;
        return source.Where(_ => _.TenantId == tenant.Id);
    }
}
```

The returned `IQueryable<T>` is composed into the query, so the filter is translated to SQL along with everything else — it is not a post-filter over materialized rows.


## Reading and writing headers

`RequestHeaders` is what the caller sent, and `ResponseHeaders` is the response being built — the live `HttpContext` dictionaries when the query came through `MapScry`, so a write is on the response by the time it is sent rather than needing to be copied there:

```cs
public sealed class AuditedPolicy :
    IReturnablePolicy<Order>
{
    public IQueryable<Order> Filter(IQueryable<Order> source, ScryPolicyContext context)
    {
        var correlation = context.RequestHeaders["X-Correlation"].ToString();
        context.ResponseHeaders["X-Scry-Policy"] = "orders";
        logger.LogInformation("Order query {Correlation}", correlation);
        return source.Where(_ => !_.Archived);
    }
}
```

A policy runs while the query is being built, which is before the response starts on either endpoint — including the streaming one, where headers are fixed once the first row is written. So a write always lands.

> [!WARNING]
> `RequestHeaders` is **attacker-controlled**. The client chooses every value in it, so a policy that scopes rows by a request header is not scoping anything — an attacker sends a different one. Use it for correlation, tracing, and diagnostics. Take identity and tenancy from the authenticated principal via `context.Services`, which the [auth middleware](security.md#what-scry-does-not-do) established.

Off the HTTP endpoint — `ScryProcessor` [hosted directly](server.md#hosting-without-the-http-endpoint) — both are empty dictionaries unless the caller supplies them, so a policy that reads one gets nothing rather than faulting.


## Registering a policy

By attribute, on the model:

```cs
[Queryable]
[ReturnableWith(typeof(ActiveOnlyPolicy))]
public class Employee { ... }
```

Or in code, which takes precedence over the attribute on the same type:

<!-- snippet: addPolicy -->
<a id='snippet-addPolicy'></a>
```cs
var response = Processor(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>()).Execute(request, context);
```
<sup><a href='/src/Scry.Tests/ExecutionTests.cs#L195-L197' title='Snippet source file'>snippet source</a> | <a href='#snippet-addPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Code registration is the better fit when the policy lives in the server project and the model project should not reference it.

Either form also covers every opted-in type deriving from the one it names. A policy is the one annotation that [inherits](annotations.md#inheritance), so a subclass cannot shed the one its base carries — which matters because an opted-in subclass is a source in its own right, queryable without naming the base. Where several apply they all narrow, base-most first, and registering one in code replaces the attribute on that same type without displacing what that type inherits.

### Inheritance runs downwards only

The converse does not hold, and the difference is load-bearing. A policy on a **derived** type is not in the base's chain, so it does not filter the base source: given a policy on `Vehicle` and none on `Asset`, `Source<Vehicle>` hides the rows it names and `Source<Asset>` returns and counts those same rows as assets. Only the members `Asset` itself exposes are readable that way — a derived type's own members stay unreachable until a query narrows to it — and narrowing does apply the policy, so `Source<Asset>.OfType<Vehicle>` matches `Source<Vehicle>` exactly.

That is deliberate: each source is its own authorization surface, and a policy filters the source it is attached to. A hierarchy where the rows must be restricted however they are reached wants the policy on the **base**, where every source below inherits it. Attaching one only to a subclass restricts that subclass's source and nothing above it.


## Ordering guarantee

The policy is applied to the source **first**, immediately after resolution and before any `Where`, `OrderBy`, `Skip`, `Take`, `GroupBy`, `Select`, or terminal from the client.

```
source → policy → client pipeline → projection → execute
```

Which means a client filter can only ever **narrow** the authorized set. There is no client expression that can widen it, reorder around it, or observe rows outside it — including through `Count`, `Any`, or an aggregate, all of which run over the policy-filtered sequence.

This test demonstrates it: the request carries no filter on `Active`, yet the inactive row never appears.

<!-- snippet: ExecutionTests.PolicyScopesRowsBeforeClientFilter.verified.txt -->
<a id='snippet-ExecutionTests.PolicyScopesRowsBeforeClientFilter.verified.txt'></a>
```txt
{
  "version": 2,
  "kind": "List",
  "payload": [
    {
      "name": "Aaron"
    },
    {
      "name": "Alice"
    },
    {
      "name": "Carol"
    }
  ],
  "stamp": "{scrubbed stamp}"
}
```
<sup><a href='/src/Scry.Tests/ExecutionTests.PolicyScopesRowsBeforeClientFilter.verified.txt#L1-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-ExecutionTests.PolicyScopesRowsBeforeClientFilter.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Reached through a navigation

A policy filters a *source*, and a navigation into that source is a second way to reach its rows. So a policy applies at the traversal too, not only where the source is the root:

<!-- snippet: navigationPolicy -->
<a id='snippet-navigationPolicy'></a>
```cs
class EngineeringOnlyPolicy :
    IReturnablePolicy<Department>
{
    public IQueryable<Department> Filter(IQueryable<Department> source, ScryPolicyContext context) =>
        source.Where(_ => _.Name == "Engineering");
}
```
<sup><a href='/src/Scry.Tests/NavigationPolicyTests.cs#L11-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-navigationPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Employee.Department` navigates into `Department`, which the policy above filters. Reading a member through that navigation reads it through the policy:

<!-- snippet: navigationPolicyQuery -->
<a id='snippet-navigationPolicyQuery'></a>
```cs
var rows = await client.Source<Employee>("Employee")
    .OrderBy(_ => _.Name)
    .Select(_ => new {_.Name, Department = _.Department!.Name})
    .ToListAsync();
```
<sup><a href='/src/Scry.Tests/NavigationPolicyTests.cs#L26-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-navigationPolicyQuery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An employee in a department the policy hides still comes back — it is `Employee` that was queried, and `Employee` carries no policy here — but the department it names reads as **null**.

That null is deliberate and is the whole point: it is indistinguishable from an absent optional navigation. The client learns there is nothing here to read, never that there is something it may not have.

The traversal is rewritten into a correlated subquery over the policy-filtered set, keyed on the navigation's own foreign key, so it stays part of the one query that was going to run:

```sql
SELECT [e].[Name], (
    SELECT TOP(1) [d].[Name]
    FROM [Departments] AS [d]
    WHERE [d].[Name] = N'Engineering' AND [e].[DepartmentId] = [d].[Id])
FROM [Employees] AS [e]
```

The cost of that is one subquery per *member read*, not per navigation: a projection naming three members of a policied navigation emits three, where an unpoliced one is a single join. A policy on a type that many queries navigate into is worth measuring.

### It applies wherever the path appears

Not only projections. Every rooted member path is rebound through one place, so the policy applies to all of them:

| Where the traversal appears | What the policy does |
| --- | --- |
| A projection leaf, or a nested projection | The value reads as null |
| A `Where` predicate | Compares against nothing, so the row does not match |
| An `OrderBy` | Sorts as null |
| A `GroupBy` key | Hidden targets group together, under a null key |
| A join key, or a member of a join's projection | As above, per side |
| An aggregate's selector inside a collection subquery | Contributes nothing |

The predicate row is the one that matters most. A predicate runs in SQL, so without the policy applied at the traversal, `Where(_ => _.Department!.Name == "Sales")` would answer — row by row — about departments a direct query of `Department` could never return. That is an oracle for hidden rows even though nothing is projected. Applying the policy at the traversal closes it.

### Nullability

A value type read through a policied traversal is widened to its nullable form, because the policy can produce a null where the model says there is always a row. A client projecting `_.Department!.Id` therefore receives null rather than an `int` for a hidden department, and should project it as `int?`.

### Collections are refused instead

This applies to reference navigations. A `[QueryableCollection]` of a policied type is a [startup failure](annotations.md#queryablecollection) rather than a rewrite: aggregating a collection cannot apply a policy — a policy filters a source, and a subquery has none — so exposing it would count exactly the rows the policy hides.

### The startup probe

Applying a policy at a traversal puts the policy's own queryable in correlated-subquery position, where a policy that composes perfectly well as a root filter can still fail to translate. So startup translates every navigation into a policied source once, and refuses to start if one does not:

```
The row policy on 'Department' does not translate where 'Employee.Department' navigates into it. A
navigation into a policied source is read through that source's policy, which puts the policy's
queryable in a correlated subquery — so it has to be composable, not merely runnable at the root.
```

Probing resolves and runs each such policy once, outside a request — with no principal, and with empty headers. A policy that cannot answer under those conditions opts out:

```cs
options.ProbePoliciedNavigations = false;
```

That gives up only the startup proof. The policy still applies at every traversal per request.


## Instantiation

The policy type is resolved from the request's `IServiceProvider` first, so it can take constructor dependencies:

```cs
builder.Services.AddScoped<TenantPolicy>();
```

If it is not registered, Scry falls back to `Activator.CreateInstance`, which requires a public parameterless constructor. If neither works the request fails:

```
Could not create policy 'TenantPolicy'.
```

When executing through `ScryProcessor.Execute(request, db)` — the overload without a service provider — only the `Activator` path is available.


## Applies to every source kind

Policies work on entities, views, and POCO sources alike; each is an `IQueryable<T>` by the time the policy sees it. For a POCO source the filter runs in memory, for the others it is translated to SQL.


## Attachment policies

An [`[Attachment]`](attachments.md) is the one thing a row policy cannot reach: its value is fetched by row key through an endpoint of its own rather than returned by a query. It has a check of its own, and unlike a row policy it is **mandatory** — a source exposing an attachment without one refuses to start.

<!-- snippet: attachmentPolicyInterface -->
<a id='snippet-attachmentPolicyInterface'></a>
```cs
public interface IAttachmentPolicy<T>
{
    bool Authorize(ScryAttachmentContext context);
}
```
<sup><a href='/src/Scry.Server/IAttachmentPolicy.cs#L15-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-attachmentPolicyInterface' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: attachmentContext -->
<a id='snippet-attachmentContext'></a>
```cs
public sealed class ScryAttachmentContext(
    IServiceProvider services,
    DbContext db,
    string member,
    IReadOnlyList<object> keyValues,
    IHeaderDictionary requestHeaders,
    IHeaderDictionary responseHeaders)
{
    /// <summary>Context for a processor hosted outside the HTTP endpoint, which has no headers.</summary>
    public ScryAttachmentContext(IServiceProvider services, DbContext db, string member, IReadOnlyList<object> keyValues) :
        this(services, db, member, keyValues, new HeaderDictionary(), new HeaderDictionary())
    {
    }

    /// <summary>The request-scoped service provider (e.g. for the current user/tenant).</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The active <see cref="DbContext"/>.</summary>
    public DbContext Db { get; } = db;

    /// <summary>The attachment member being fetched, as the schema names it.</summary>
    public string Member { get; } = member;

    /// <summary>
    /// The primary key of the row the value hangs off, parsed into the key members' own CLR types and
    /// ordered by member name — the order the schema derives, not the order EF declares.
    /// </summary>
    /// <remarks>
    /// The row has not been read at this point, and may not exist: these are the key a caller asked
    /// for, not one taken from a row. Authorizing on them alone is the fast path; a decision needing
    /// the row itself can read it through <see cref="Db"/>.
    /// </remarks>
    public IReadOnlyList<object> KeyValues { get; } = keyValues;

    /// <summary>
    /// The headers the caller sent. Client-supplied and therefore untrusted — hint data, never an
    /// authorization input.
    /// </summary>
    public IHeaderDictionary RequestHeaders { get; } = requestHeaders;

    /// <summary>The headers of the response being built. Writes here reach the client.</summary>
    public IHeaderDictionary ResponseHeaders { get; } = responseHeaders;
}
```
<sup><a href='/src/Scry.Server/ScryAttachmentContext.cs#L7-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-attachmentContext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: attachmentPolicy -->
<a id='snippet-attachmentPolicy'></a>
```cs
public sealed class UnsealedContractsPolicy :
    IAttachmentPolicy<Contract>
{
    /// <summary>The seeded row this refuses, so a denial is exercised without needing a header.</summary>
    public const int SealedId = 3;

    public bool Authorize(ScryAttachmentContext context) =>
        context.KeyValues is not [SealedId];
}
```
<sup><a href='/src/Scry.Tests/TestModel.cs#L366-L376' title='Snippet source file'>snippet source</a> | <a href='#snippet-attachmentPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Attached with `[AttachmentWith(typeof(...))]` or `ScryOptions.AddAttachmentPolicy<TEntity, TPolicy>()`, and inherited down the base chain exactly as a row policy is, so a subclass cannot shed the check its base carries.

It differs from a row policy in three ways worth knowing:

| | Row policy | Attachment policy |
| --- | --- | --- |
| Shape | Narrows an `IQueryable<T>` | Answers yes or no |
| How many apply | Every one in the chain, all narrowing | Exactly one — the nearest declaration |
| Optional | Yes | No |

There is one rather than a chain because the check is a decision, not a filter: composing several would only raise the question of what a disagreement means.

**Both still apply.** The fetch resolves its row through the policy-filtered source, so a row a query could not have returned is not one an attachment can be pulled from — the attachment check narrows what is already authorized, exactly as a client filter does. A refusal by either is the same `404` as a row that was never there; see [Security](attachments.md#security).

The check runs **before the database is touched**, so an unauthorized caller learns nothing, not even how long a lookup took. `KeyValues` holds the key it asked for, parsed into the key members' own types — a key, not a row, since the row has not been read and may not exist. A decision needing the row itself can read it through `Db`.


## What a policy is not

- **Not authentication or authorization.** Establish the caller's identity on the endpoint (`MapScry("/api/query").RequireAuthorization()`); a policy consumes that identity, it does not verify it.
- **Not column security.** A policy filters *rows*. To hide a column, use `[QueryIgnore]` — see [Annotations](annotations.md).
- **Not a per-query hook.** It is applied uniformly to every query over that source. There is no way for a client to opt out of it or influence it.
