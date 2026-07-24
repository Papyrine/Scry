# Writing queries

Client queries are ordinary C# LINQ written against the generated query models. Nothing runs
client-side: the expression tree is **captured**, translated to the [wire AST](wire-format.md), and
sent to the server when a terminal operator is awaited.

snippet: clientQuery

The supported surface is deliberately closed. Anything outside it fails fast with a clear
`NotSupportedException` at translation time — before a request is ever sent.

## Entry points

The generated `ScryQuery` exposes one property per allow-listed source:

snippet: GeneratorTests.EntitiesViewPocoAndEnum#ScryQuery.g.verified.cs

Each returns an `IQueryable<T>` whose provider only captures. Enumerating it synchronously throws:

```
Use ToListAsync to execute a Scry query.
```

You can also reach a source by name, which is what the generated code does under the hood:

snippet: scryClientApi

## Operators

| LINQ | Wire op | Notes |
| --- | --- | --- |
| `Where(predicate)` | `where` | Not allowed after `GroupBy` or `Select`. |
| `OrderBy(key)` / `OrderByDescending(key)` | `orderBy` | Key must be a scalar member. Not allowed after `GroupBy` or `Select`. |
| `ThenBy(key)` / `ThenByDescending(key)` | `thenBy` | Must follow an `OrderBy`. |
| `Skip(n)` | `skip` | `n` must be non-negative. |
| `Take(n)` | `take` | `n` must be non-negative and `<= MaxPageSize`. |
| `GroupBy(key)` | `groupBy` | Exactly one key, at most one `GroupBy`, and it must be followed by a `Select`. |
| `Select(projection)` | `select` | At most one, and it must construct an object. |

Any other LINQ operator — `Join`, `Distinct`, `SelectMany`, `Union`, `Last`, `Reverse`, … — throws:

```
LINQ operator 'Join' is not supported by Scry.
```

Operators are applied left to right, exactly as written.

## Terminals

Terminals are `async` extension methods on `IQueryable<T>` from `Scry.Client`. Awaiting one is what
sends the request.

| Method | Returns | Result kind |
| --- | --- | --- |
| `ToListAsync()` | `Task<List<T>>` | list |
| `FirstAsync()` | `Task<T?>` | single |
| `FirstOrDefaultAsync()` | `Task<T?>` | single |
| `SingleAsync()` | `Task<T?>` | single |
| `SingleOrDefaultAsync()` | `Task<T?>` | single |
| `CountAsync()` | `Task<int>` | scalar |
| `AnyAsync()` | `Task<bool>` | scalar |

Each takes an optional `CancellationToken`. There are no predicate overloads on the client — filter
with `Where` first:

```cs
var count = await Query.Employee
    .Where(_ => _.Active)
    .CountAsync();
```

`FirstAsync` and `SingleAsync` are declared as `Task<T?>` even though the server throws when the
sequence is empty; the nullable return covers the `OrDefault` variants without a second signature.

There is one non-executing terminal, used by tooling such as the [explorer](explorer.md):

snippet: translateWithoutExecuting

which produces the wire request without contacting the server.

## Expressions

Everything below may appear inside a `Where` predicate, an ordering key, a group key, an aggregate
selector, or a projection leaf, subject to the position rules further down.

### Member access

A path rooted at the lambda parameter, traversing reference navigations:

```cs
.Where(_ => _.Manager!.Department!.Name == "Engineering")
```

becomes the member path `["Manager", "Department", "Name"]`. Path length is capped by
`MaxNavigationDepth` (default 4). Collection navigations are not exposed at all, so there is no way
to express a traversal into one.

### Operators

snippet: wireBinaryOps

C# `==`, `!=`, `<`, `<=`, `>`, `>=`, `&&`, `||`, `+`, `-`, `*`, `/`, `!`, and unary `-` map onto
these. Any other operator (`%`, `&`, `|`, `^`, `<<`, `>>`, `??`, `?:`) throws:

```
Binary operator 'Modulo' is not supported by Scry.
```

`Convert` / `ConvertChecked` nodes — which the C# compiler inserts freely around enums, nullables,
and numeric widening — are transparently unwrapped rather than encoded.

### Functions

snippet: wireFunctions

Mapped from:

| C# | Function |
| --- | --- |
| `text.Contains(value)` | `StringContains` |
| `text.StartsWith(value)` | `StringStartsWith` |
| `text.EndsWith(value)` | `StringEndsWith` |
| `text.ToLower()` | `StringToLower` |
| `text.ToUpper()` | `StringToUpper` |
| `string.IsNullOrEmpty(text)` | `StringIsNullOrEmpty` |
| `date.Year` | `DateYear` |
| `date.Month` | `DateMonth` |
| `date.Day` | `DateDay` |

The date parts apply to `DateTime` and `DateOnly`. There is no free-form method call node in the
wire format, so this list is the complete set of behaviour a client can ask the database to perform.

### Constants and captured values

Literals become constants. So does **any sub-expression that does not reference the lambda
parameter** — it is evaluated on the client and sent as a constant. That is what makes a query
parameterizable at runtime:

snippet: clientClosureCapture

`status` and `top` are locals; the translator compiles and invokes those sub-expressions, then emits
their values. Calls to your own methods are fine on this path as long as they do not touch the query
parameter — `.Where(_ => _.Name == BuildName())` sends the *result* of `BuildName()`.

A constant is carried as an invariant-culture string plus a type tag, and reconciled against the
member type at the comparison site on the server. Enums travel as their **name**, not their numeric
value.

## Projections

A `Select` must construct an object. Anonymous types, records, and object initializers all work:

```cs
.Select(_ => new { _.Name, Manager = _.Manager!.Name })
.Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
.Select(_ => new EmployeeRow { Name = _.Name, Department = _.Department!.Name })
```

Projecting a bare value does not work:

```cs
.Select(_ => _.Name) // NotSupportedException
```

```
A projection must construct an object (anonymous type, record, or object initializer).
```

For a record or constructor call, member names come from the constructor parameter names, capitalized
— `new EmployeeRow(name: ...)` produces the member `Name`.

Every projection leaf must resolve to an allow-listed **scalar**. A navigation cannot be projected
whole; project the scalar you want out of it (`_.Department!.Name`).

### Without a `Select`

If no projection is supplied, the server returns every allow-listed **scalar** member of the source.
Navigations are excluded, and so is anything `[QueryIgnore]`d — which is why the default projection
of `Employee` never contains `Salary`.

### Nested result objects

The wire format can nest a projection under a navigation, producing nested JSON:

snippet: ExecutionTests.WhereOrderByNestedProjection.verified.txt

The client translator always emits flat member paths, so this shape is reachable by constructing the
AST directly rather than from LINQ. From LINQ, name the leaf instead —
`Department = _.Department!.Name`.

## Grouping and aggregates

`GroupBy` must be followed by `Select`, and that projection may reference **only** the group key and
aggregates:

snippet: clientGroupBy

snippet: wireAggregates

| C# | Aggregate |
| --- | --- |
| `_.Count()` | `Count` |
| `_.Sum(_ => _.Amount)` | `Sum` |
| `_.Average(_ => _.Amount)` | `Average` |
| `_.Min(_ => _.Amount)` | `Min` |
| `_.Max(_ => _.Amount)` | `Max` |

Constraints:

- Exactly one group key, and it must be a member access.
- `Where`, `OrderBy`, and `ThenBy` are not allowed after `GroupBy`.
- Nested projections are not allowed in a grouped `Select`.
- Referencing a non-key member throws
  `A grouped projection may only use the group key or aggregates.`
- Aggregates outside a grouped `Select` are rejected server-side with
  `Aggregates are only allowed in a Select following GroupBy.`

## Ordering rules

The pipeline is validated as a whole. In order:

1. `Where`, `OrderBy`/`ThenBy`, `Skip`, `Take` — any number, in any order, before grouping or
   projection.
2. At most one `GroupBy`, which must precede the `Select`.
3. At most one `Select`.
4. At most one terminal, and nothing after it.

`ThenBy` without a preceding `OrderBy` is rejected. So is any operator after a terminal.

## Errors

| Where | Type | When |
| --- | --- | --- |
| Client, at translation | `NotSupportedException` | The LINQ cannot be expressed in the wire AST. Nothing is sent. |
| Client, on response | `ScryRequestException` | The server returned a non-success status. Carries `StatusCode` and `Body`. |
| Client, on response | `ScryWireException` | The response could not be deserialized, or its result kind did not match the terminal. |
| Server, during validation | `ScryValidationException` → `400` | The request violates the allow-list or a limit. |

See [Server](server.md#error-handling) for the response bodies.
