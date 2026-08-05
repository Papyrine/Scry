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
<sup><a href='/src/Scry.Tests/TestModel.cs#L305-L313' title='Snippet source file'>snippet source</a> | <a href='#snippet-returnablePolicy' title='Start of snippet'>anchor</a></sup>
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


## What a policy is not

- **Not authentication or authorization.** Establish the caller's identity on the endpoint (`MapScry("/api/query").RequireAuthorization()`); a policy consumes that identity, it does not verify it.
- **Not column security.** A policy filters *rows*. To hide a column, use `[QueryIgnore]` — see [Annotations](annotations.md).
- **Not a per-query hook.** It is applied uniformly to every query over that source. There is no way for a client to opt out of it or influence it.
