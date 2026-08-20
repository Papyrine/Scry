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


## What a denied row produces

By default a denied row is not there at all: absent from a list, missing from a single-row result, null through a navigation. That is the only answer that discloses nothing — a caller cannot tell a row it may not see from one that never existed.

Where that silence is worse than the disclosure, a policy can be told to fail the request instead. The choice is made per policy and per position, because a denial means something different depending on where it lands:

| Position | Default | `Error` |
| --- | --- | --- |
| `RootSingle` — `First`, `Single`, `Last` | the result is empty | HTTP 403 |
| `RootList` — lists, pages, streams, counts, aggregates | the row is missing | HTTP 403 |
| `Navigation` — a traversal into the source | the traversal reads null | HTTP 403 |
| `CollectionNavigation` — a `[QueryableCollection]` of it | `Refuse`: startup failure | HTTP 403 |

On the model:

```cs
[Queryable]
[ReturnableWith(typeof(ActiveOnlyPolicy), RootList = DeniedRowMode.Error)]
public class Employee { ... }
```

Or in code:

```cs
options.AddPolicy<Employee, ActiveOnlyPolicy>(new()
{
    RootList = DeniedRowMode.Error
});
```

The response is a 403 carrying one fixed message — `The query was denied by a server policy.` — which names no source, member, row, or policy. Erroring already discloses that something matched; naming what would disclose the shape of the policy on top of it. Clients raise `ScryPermissionException` for it, on the query, stream, and batch paths alike; in a batch it is one entry's result and the others are answered normally. The 403 is never cached, and the outcome is recorded as `Denied` rather than as a failure — a mode that discloses existence is worth being able to count.

### When it fires

For the two root positions, the request fails if a row the caller could see once every *hiding* policy has run is one an *erroring* policy denies. A row already hidden by another policy is therefore never reported: an error says what this caller lost to this policy, not what it was never going to be shown.

The rows in question are the ones the query asked for, narrowed by the filters written before the first paging or flattening operator. So a client filter that excludes the denied row means there is nothing to report, while `Take(1)` does not — the answer does not depend on where in the result the denied row happened to fall.

For the two navigation positions the question is asked of the relationship rather than of one query: whether any row of the owner source, filtered by its own policies, reaches a denied row that way. A navigation is read per row and which rows those are depends on the whole shape of the query — including filters written over the traversal itself — so narrowing by them would make the answer depend on the question in a way a caller could use to probe it. Erring wide costs a request that would have succeeded; erring narrow returns a row the deployment asked to be told about.

All of this is asked before anything executes, so a denied row never reaches a result and a stream fails before its first byte. A [SQL preview](explorer.md) runs no query and so denies nothing.

> [!WARNING]
> `Error` is a deliberate existence oracle: a 403 tells a caller that rows it may not see matched its query, which is exactly the signal hiding exists to withhold. Enable it only where "you lack permission" is itself not sensitive — never on a source whose row existence is the secret. See [security.md](security.md).


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

### Collections

A `[QueryableCollection]` of a policied type is a [startup failure](annotations.md#queryablecollection) by default: aggregating a collection off its owner cannot apply a policy — a policy filters a source, and a subquery has none — so exposing it would count exactly the rows the policy hides. It is refused rather than guessed at, because either answer could be the one a deployment wants.

Saying which unlocks it:

```cs
options.AddPolicy<OrderLine, BulkLinesOnlyPolicy>(new()
{
    CollectionNavigation = DeniedCollectionMode.Hide
});
```

`Hide` reads the collection through the same correlated subquery a reference navigation already uses, so `_.Lines.Count()` counts what a direct query of `OrderLine` would have reached, `Sum` totals only those, and `SelectMany` flattens to exactly them. `Error` fails the request instead — see [What a denied row produces](#what-a-denied-row-produces). Any policy in the chain left at `Refuse` refuses the member, since the chain is only as readable as its least permissive link.

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


## When the decision is too expensive for SQL

Everything above assumes the policy can be written as a filter the database evaluates per row. Some permission logic cannot: it calls into domain code, walks a hierarchy, consults another system. Running it inline would make every query pay for it.

`ICachedRowPolicy<T>` answers one row at a time in C# instead. The server remembers the answers per caller and composes what the query actually carries — a membership test over the keys that caller may see. The [sample](sample.md#a-policy-too-expensive-to-run-per-row) runs this end to end:

<!-- snippet: cachedRowPolicy -->
<a id='snippet-cachedRowPolicy'></a>
```cs
/// <summary>
/// Scopes <see cref="Order"/> to the regions the caller is granted. Written as a cached policy rather
/// than an ordinary <c>IReturnablePolicy</c> because the decision is a lookup against another system —
/// far too slow to run per row inside every query, and unchanging often enough to be worth remembering.
/// </summary>
public sealed class RegionAccessPolicy(RegionGrants grants) :
    ICachedRowPolicy<Order>
{
    /// <summary>
    /// Which set of answers this call belongs to. The sample has no sign-in, so there is one caller and
    /// one scope, exactly as <c>CacheScope</c> has one; a real app returns the tenant or the principal
    /// resolved from <c>context.Services</c>. Never from a request header — decisions are remembered
    /// per scope, so a caller choosing its own scope key is a caller choosing its own permissions.
    /// </summary>
    public string ScopeKey(ScryPolicyContext context) => "sample";

    /// <summary>
    /// The expensive part. It runs off the query path — for a row that is new, one whose
    /// <see cref="Order.Revision"/> has moved, and every row the first time a scope is read — and never
    /// again just because a query ran.
    /// </summary>
    public bool Allow(Order row, string scopeKey, ScryPolicyContext context) =>
        grants.Allows(scopeKey, row.Region);
}
```
<sup><a href='/samples/Sample.Server/RegionAccessPolicy.cs#L1-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-cachedRowPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Registered with the column that says a row has changed:

<!-- snippet: addCachedPolicy -->
<a id='snippet-addCachedPolicy'></a>
```cs
// A row policy whose decision is too slow to run per row in SQL, so it runs in C# and
// the server remembers what it answered. Revision is what tells it a row has changed
// and needs deciding again — see /docs/policies.md and the /permissions page.
_.AddCachedPolicy<Order, long, RegionAccessPolicy>(_ => _.Revision);
```
<sup><a href='/samples/Sample.Server/Program.cs#L45-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-addCachedPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The adapter is an ordinary `IReturnablePolicy<T>` underneath, so everything on this page still holds: it applies at the root, at a join's inner side, at a narrowing, at a membership test and at a traversal, it narrows alongside any other policy on the chain, and it takes the same `DeniedRowHandling`.

### Keeping it current

A cache that decided too often would not be one; a cache that decided too rarely would hand over a row nobody had ruled on. Three things move it:

- **A new or changed row is decided on its first read.** Each scope remembers how far it has decided — the highest version it has seen — and the rows past that are the ones still owed an answer. An insert by any writer, including one that never went through this server, is therefore correct the first time it is read, and no query rescans the table to establish that. **Index the version column**: every refresh runs one `WHERE version > @watermark` against it.
- **A grant changing is something no column can see**, so the host says so. `InvalidateRows` re-decides those rows for every caller; `InvalidateScope` forgets one caller entirely. Both take effect on the next read — this is the lag the cache trades for the cost it avoids.
- **`Prime` decides rows ahead of anyone reading them**, which is what to call right after writing them, so the work does not land on whoever queries next.

Invalidating is the one of the three a host has to remember, because nothing else can notice — the sample's endpoint for it:

<!-- snippet: invalidateCachedPolicy -->
<a id='snippet-invalidateCachedPolicy'></a>
```cs
// A grant moved. Nothing about any order changed, so no version column could notice and no
// query would ever decide those rows again — the cache has to be told, and telling it is part
// of the authorization path rather than a cache optimization.
app.MapPost("/api/grants/{region}", (string region, bool allowed, RegionGrants grants, ScryPolicyCache cache) =>
{
    grants.Set("sample", region, allowed);
    cache.InvalidateScope<Order>("sample");
    return Results.NoContent();
});
```
<sup><a href='/samples/Sample.Server/Program.cs#L93-L103' title='Snippet source file'>snippet source</a> | <a href='#snippet-invalidateCachedPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Priming is `cache.Prime(scopeKey, rows, context)` alongside the write that produced them. `ScryPolicyCache` is registered as a singleton by `AddScry`, and is also `ScryProcessor.PolicyCache`.

> [!WARNING]
> **Invalidating the policy is not enough if [conditional requests](caching.md) are on.** `QueryFreshness` watches the *database*, and the authorization data behind a cached policy usually does not live there. A grant that changes outside the database moves no freshness token, so a caller holding an `ETag` is answered `304` and keeps rendering the rows it has just lost — the query never runs, and the invalidation never reaches it.
>
> Whatever a decision depends on therefore has to be in `CacheScope`, which is the part of the ETag the host controls. The sample folds a version stamp into it that moves whenever a grant does:
>
> ```cs
> _.CacheScope = _ => $"sample-{_.RequestServices.GetRequiredService<RegionGrants>().Version}";
> ```
>
> That is the same rule caching.md already states — *anything a response varies by must be in the scope* — and a cached policy is the case where it is easiest to miss, because the thing it varies by is deliberately not in the database.

Nothing has to be called for the first of the three. The sample moves a row's version and the next query decides that row and no other:

<!-- snippet: cachedPolicyReadThrough -->
<a id='snippet-cachedPolicyReadThrough'></a>
```cs
// A row changed. Nobody tells the cache anything here: the next query sees a revision past the
// watermark this scope was decided up to, and decides that one row on the spot. An insert by
// any writer at all is correct on its first read for the same reason.
app.MapPost("/api/orders/{id:int}/touch", async (int id, SampleContext data) =>
{
    var order = await data.Orders.FindAsync(id);
    if (order is null)
    {
        return Results.NotFound();
    }

    // Named explicitly: Scry's async terminals and EF's are both in scope here, and they are
    // not the same method — this one has to run against the database.
    order.Revision = await EntityFrameworkQueryableExtensions.MaxAsync(data.Orders, _ => _.Revision) + 1;
    await data.SaveChangesAsync();
    return Results.NoContent();
});
```
<sup><a href='/samples/Sample.Server/Program.cs#L105-L123' title='Snippet source file'>snippet source</a> | <a href='#snippet-cachedPolicyReadThrough' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`InvalidateRows<T>(keys)` is the narrower form of the second: it re-decides those rows in every scope, rather than emptying one scope entirely.

### The scope key

Decisions are remembered per scope and shared by every request naming the same one, so `ScopeKey` must identify the caller's authority and nothing else — two callers given the same key see the same rows. Resolve it from `context.Services`, never from `context.RequestHeaders`: a caller choosing its own scope key is a caller choosing its own permissions.

### Cost and shape

A cold scope decides every row once, serialized per scope so a burst of requests pays it once rather than each. Every allowed key then travels to the database with each query — bound as a single parameter, not written into the SQL — which suits an allow-list bounded per caller and not one that grows with the table. `options.MaxCachedPolicyKeys` turns an allow-list that quietly grew unbounded into a message rather than a slow query.

The rows are decided over the raw set, not through the source's other policies: a decision is shared between callers, so narrowing what it is made over by one caller's view would bake that view into an answer the others go on to read. The other policies still apply to the query itself, where they belong.

Answers live in `MemoryCachedPolicyStore` by default — this process, for as long as it runs, so each server warms its own and a restart decides every row again. A deployment where that costs too much implements `ICachedPolicyStore` and sets `options.CachedPolicyStore`.

The type needs a single-member primary key, derived the way an [attachment's](attachments.md) is and checked against the real one at startup; a POCO source or a keyless view has nowhere to file answers and is refused. The version column need not be exposed to clients — `[QueryIgnore]` it and it stays server-side machinery.


## What a policy is not

- **Not authentication or authorization.** Establish the caller's identity on the endpoint (`MapScry("/api/query").RequireAuthorization()`); a policy consumes that identity, it does not verify it.
- **Not column security.** A policy filters *rows*. To hide a column, use `[QueryIgnore]` — see [Annotations](annotations.md).
- **Not a per-query hook.** It is applied uniformly to every query over that source. There is no way for a client to opt out of it or influence it.
