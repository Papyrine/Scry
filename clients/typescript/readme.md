# Scry for TypeScript

A TypeScript client for Scry, in progress. What exists so far is the generator: it reads a server's introspection and writes the types a query is written against, reading the description the way the [query explorer](../../docs/explorer.md) does. Writing and sending queries comes next.

The package is private and unpublished.


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
