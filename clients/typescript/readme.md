# Scry for TypeScript

A TypeScript client for Scry, in progress. What exists so far:

- the generator, which reads a server's introspection and writes the types a query is written against, reading the description the way the [query explorer](../../docs/explorer.md) does;
- the query builder, which writes the request a C# client sends for the same query, byte for byte.

Sending a request over HTTP and reading its rows back comes next, then streams, live queries and commands. Until then a query is sent through a `Transport` the caller supplies.

The package is private and unpublished.


## Writing a query

```ts
import { createScry, and, eq, startsWith } from "scry-client";
import { schema, type Sources } from "./scry.generated.ts";

const scry = createScry<Sources>(schema, { transport });

const rows = await scry.employee
  .where(_ => and(_.active, eq(_.status, "FullTime"), startsWith(_.manager.name, "A")))
  .orderBy(_ => _.name)
  .select(_ => ({ name: _.name, manager: _.manager.name, department: { name: _.department.name } }))
  .toList();
// { name: string; manager: string | null; department: { name: string | null } }[]
```

A member is read as a path, so `_.manager.name` is the `Manager.Name` the server reads. Comparison, logic and arithmetic are functions (`eq`, `gt`, `and`, `not`, `add`, `coalesce`, `cond`, `concat`, `isIn` and the rest), as is every function the wire carries (`startsWith`, `year`, `addDays`, `round`, `toDouble`…). A collection is asked about through its own methods: `_.lines.any(l => gt(l.quantity, 1))`, `_.tags.contains("urgent")`. Grouping, joins and set operations follow LINQ's shape: `groupBy(_ => _.region).select(g => ({ key: g.key, total: g.sum(_ => _.amount) }))`.

The types follow the server's pipeline grammar, so an ordering the server would refuse does not compile: `thenBy` follows an ordering, a grouped query projects, nothing filters after a projection, and a terminal's own condition comes before any projection.

A constant is typed by what stands beside it, as C# would type it, so a query sends the bytes the same C# query sends: `gt(_.amount, 90)` sends `90` as a `Decimal` because `Amount` is one. A projected key travels with its first letter capitalized, as a C# member is named, and rows read back under the key as written.

JavaScript's own operators ask nothing of the server, since a member is an expression rather than a value. `>`, `+` and a template literal on one throw. `&&`, `||`, `!` and `? :` cannot be caught at run time: `where(_ => a && b)` would send `b` alone. Turn on `@typescript-eslint/strict-boolean-expressions` for code that writes queries.

`createScry` takes two more options. `defaultProjection: "server"` stops a query projecting nothing from naming the model's members, as a C# source built by hand does, and `stamp: false` leaves the schema stamp off each request.


## Generating a client

```bash
node src/codegen/cli.ts --input http://localhost:5000/scry/introspect --output src/scry.generated.ts
```

| Option | |
| --- | --- |
| `--input` | The server's introspection: a JSON file, or the URL the explorer reads it from (`{explorer route}/introspect`, mapped in Development by default). |
| `--output` | Where to write the generated TypeScript. |
| `--runtime` | The module generated code imports the client's types from. Defaults to `scry-client`. |

A description from another introspection version is refused, as is a type display the generator does not know: generating against a newer server fails loudly rather than guessing.


## What is generated

Per model, two interfaces:

- `{Type}Query`, what a query reads. Every member is an `Expr` of the value it holds, a navigation is `Optional` (every value read through it may be null), and a collection is a `Collection` asked about through a subquery. An attachment is left out, since no query reads one.
- `{Type}Row`, the row a query projecting nothing returns: the model's scalar members, a base's first.

Beside them: every enum the model names (a [Flags] one as `Flags`), `Sources` and `Commands` mapping each to its types, and `schema`, which says at run time what the types cannot: the name each member travels under, what each value is, and what a query projecting nothing projects.

| Server type | TypeScript |
| --- | --- |
| `bool` | `boolean` |
| `string`, `char` | `string` |
| `int` and the smaller integers, `uint`, `float`, `double` | `number` |
| `long`, `ulong` | `bigint` |
| `decimal` | `Decimal`, its invariant text |
| `Guid` | `Guid`, its text |
| `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset` | Each as its round-trip text. A `DateTime` is a wall clock, so it is not a JavaScript `Date`. |
| `byte[]` | `Uint8Array` |
| An enum | A union of its names |
| `T?` | `T \| null` |

Introspection does not say whether a `string` or `byte[]` member may be null, so neither is typed as nullable, as the C# client's generated models do not mark them either.


## Developing

Node 23.6 or later runs TypeScript as it is, so there is no build step.

```bash
npm ci
npm run check
```

`check` type-checks, which also runs the assertions in `test/types.ts`, and runs the tests. The tests read fixtures the .NET side keeps as snapshots in `src/Scry.Tests/TypeScript` — the wire vocabulary, the test model's description, the request corpus — so a change there reaches this client as a failing test.

What is generated for the two fixture descriptions is committed under `test/generated`. After changing the generator, or after the .NET side accepts a new description, write it again and review the difference:

```bash
npm run fixtures
```
