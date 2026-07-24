# Row policies

A row policy narrows a source to the rows the caller is allowed to see, **before** any client
operator is applied. It is the mechanism for tenant scoping, soft delete, and row-level security.

snippet: returnablePolicyInterface

## Writing a policy

snippet: returnablePolicy

The `context` carries the request-scoped service provider and the active `DbContext`:

snippet: policyContext

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

The returned `IQueryable<T>` is composed into the query, so the filter is translated to SQL along
with everything else — it is not a post-filter over materialized rows.

## Registering a policy

By attribute, on the model:

```cs
[Queryable]
[ReturnableWith(typeof(ActiveOnlyPolicy))]
public class Employee { ... }
```

Or in code, which takes precedence over the attribute on the same type:

snippet: addPolicy

Code registration is the better fit when the policy lives in the server project and the model project
should not reference it.

## Ordering guarantee

The policy is applied to the source **first**, immediately after resolution and before any `Where`,
`OrderBy`, `Skip`, `Take`, `GroupBy`, `Select`, or terminal from the client.

```
source → policy → client pipeline → projection → execute
```

Which means a client filter can only ever **narrow** the authorized set. There is no client
expression that can widen it, reorder around it, or observe rows outside it — including through
`Count`, `Any`, or an aggregate, all of which run over the policy-filtered sequence.

This test demonstrates it: the request carries no filter on `Active`, yet the inactive row never
appears.

snippet: ExecutionTests.PolicyScopesRowsBeforeClientFilter.verified.txt

## Instantiation

The policy type is resolved from the request's `IServiceProvider` first, so it can take constructor
dependencies:

```cs
builder.Services.AddScoped<TenantPolicy>();
```

If it is not registered, Scry falls back to `Activator.CreateInstance`, which requires a public
parameterless constructor. If neither works the request fails:

```
Could not create policy 'TenantPolicy'.
```

When executing through `ScryProcessor.Execute(request, db)` — the overload without a service
provider — only the `Activator` path is available.

## Applies to every source kind

Policies work on entities, views, and POCO sources alike; each is just an `IQueryable<T>` by the time
the policy sees it. For a POCO source the filter runs in memory, for the others it is translated to
SQL.

## What a policy is not

- **Not authentication or authorization.** Establish the caller's identity on the endpoint
  (`MapScry("/api/query").RequireAuthorization()`); a policy consumes that identity, it does not
  verify it.
- **Not column security.** A policy filters *rows*. To hide a column, use `[QueryIgnore]` — see
  [Annotations](annotations.md).
- **Not a per-query hook.** It is applied uniformly to every query over that source. There is no way
  for a client to opt out of it or influence it.
