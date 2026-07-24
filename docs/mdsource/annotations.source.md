# Annotations

`Scry.Annotations` (namespace `Scry`, targeting `netstandard2.0`) holds the attributes that define
the allow-list. They are applied to the **server** model. Both the source generator and the server
runtime read the same attributes and derive the same surface from them.

The model is **default-deny**: a type that carries none of the opt-in attributes is invisible to
clients, and a request naming it is rejected as an unknown source.

| Attribute | Target | Effect |
| --- | --- | --- |
| `[Queryable]` | class, struct | Opts a table-backed EF Core entity into client querying. |
| `[QueryableView]` | class, struct | Opts a keyless EF Core entity (a database view) into client querying. |
| `[QueryablePoco]` | class, struct | Opts a non-persisted POCO into client querying; the server supplies the data. |
| `[QueryIgnore]` | property, field | Excludes a member from an opted-in type. |
| `[ReturnableWith(typeof(TPolicy))]` | class, struct | Attaches a server-side row policy. |

## `[Queryable]`

snippet: queryableEntity

The source name exposed to clients is the **type name** — `Employee`. That is what appears as the
`root` of a wire request, as the property name on the generated `ScryQuery`, and in the introspection
output.

If the type also carries EF Core's `[Keyless]`, Scry classifies it as a view rather than an entity.
The two are resolved identically (`DbContext.Set<T>()`); the distinction is reported through
introspection so tooling can label it.

> The `Name` property on `[Queryable]`, `[QueryableView]`, and `[QueryablePoco]` is declared but not
> yet honoured. In the current version both the generator and the server derive the source name from
> the type name unconditionally. Renaming a source is not supported yet.

## `[QueryableView]`

For a keyless entity mapped to a database view:

snippet: queryableView

paired with the usual EF configuration on the context:

snippet: dbContext

`[QueryableView]` is equivalent to putting `[Queryable]` on a type that EF has marked `[Keyless]`;
use it when the keyless configuration lives in `OnModelCreating` rather than on the type.

## `[QueryablePoco]`

For a type that is not part of the persisted model at all:

snippet: queryablePoco

The server must supply the data:

snippet: serverRegistration

The registered sequence is turned into an `IQueryable` with `AsQueryable()`, so the pipeline runs
in-memory over LINQ to Objects, applying the same validation and shaping as a database source.

Registration is **mandatory**. `AddScry` throws at startup if a `[QueryablePoco]` type has no
registered source:

```
POCO source 'Holiday' has no data registered. Call options.AddPocoSource<Holiday>(...).
```

See [Server](server.md#poco-sources) for the per-request factory overload.

## `[QueryIgnore]`

```cs
[QueryIgnore]
public decimal Salary { get; set; }
```

An ignored member is excluded twice over:

- The source generator never emits it, so client code cannot name it and there is no IntelliSense
  entry for it.
- The server's schema never registers it, so a hand-crafted request naming it is rejected with
  `Property 'Salary' is not allow-listed on 'Employee'.`

It is also absent from the default projection — a query with no `Select` returns every allow-listed
scalar, and `Salary` is not one.

## `[ReturnableWith]`

```cs
[ReturnableWith(typeof(ActiveOnlyPolicy))]
public class Employee { ... }
```

Names an `IReturnablePolicy<T>` implementation that the server applies to the source **before** any
client operator. It is server-only: the generator ignores it and the client never sees it. See
[Row policies](policies.md).

A policy registered in code via `ScryOptions.AddPolicy<TEntity, TPolicy>()` takes precedence over the
attribute on the same type.

## Which members are exposed

A member of an opted-in type is exposed when **all** of the following hold:

- It is a property (fields are never exposed).
- It has a **public instance getter**.
- It takes no index parameters.
- It does not carry `[QueryIgnore]`.
- Its type is either a **scalar** or a **reference navigation to another opted-in type**.

Everything else is silently excluded — no error, it simply does not appear.

### Scalars

`bool`, `char`, `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`,
`double`, `decimal`, `string`, `DateTime`, `DateOnly`, `TimeOnly`, `DateTimeOffset`, `TimeSpan`,
`Guid`, and any `enum` — plus the `Nullable<>` form of each value type.

An `enum` used by an exposed member is re-emitted into the generated client code (as
`ScryEnums.g.cs`), so the client can compare against it without referencing the model.

Scalars can be used in predicates, ordering keys, group keys, aggregate selectors, and projection
leaves.

### Navigations

A property whose type is another opted-in type is a **reference navigation**. It can be traversed in
a member path (`e.Manager.Name`) and projected into, up to `MaxNavigationDepth` segments (default 4).

A navigation cannot itself be a value — it cannot be compared, ordered by, grouped by, or used as a
projection leaf. `Projection member must reference a scalar value.` is the rejection you get.

### Not exposed

- **Collection navigations** (`List<Employee> Employees`). There is no wire node for traversing a
  collection, so a client cannot fan out, `Any()` into a child set, or aggregate across one. If you
  need a collection-derived value, expose it as a view or a computed scalar on the parent.
- **Complex types that are not themselves opted in.** Adding `[Queryable]` to the target type makes
  it traversable.
- **Write-only or non-public properties, indexers, and fields.**

## Keeping the two readers aligned

Two independent components read the same attributes:

- `MetadataModelReader` in the generator, over `System.Reflection.Metadata`, at build time.
- `ScrySchema` in the server, over `System.Reflection`, at startup.

They deliberately agree on classification and on the C# type spelling each member gets — the server's
introspection output reproduces the generator's emission exactly, which is what lets the
[query explorer](explorer.md) synthesize an identical model in the browser. The server's copy is the
one that matters for security: it is rebuilt at runtime from the real assembly and validates every
request regardless of what the client was generated against.
