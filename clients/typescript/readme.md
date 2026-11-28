# Scry for TypeScript

A TypeScript client for Scry, in progress. What exists so far:

- the generator, which reads a server's introspection and writes the types a query is written against, reading the description the way the [query explorer](../../docs/explorer.md) does;
- the query builder, which writes the request a C# client sends for the same query, byte for byte;
- the HTTP transport, and the reader that turns each answer back into what the query said it returns;
- streams, batches, and live queries that reconnect;
- commands, followed to their outcome, and what a caller may send.

Binary parts and attachments come next.

The package is private and unpublished.


## Sending a query

```ts
const scry = createScry<Sources>(schema, {
  transport: httpTransport({ endpoint: "https://example.test/api/query", headers: () => ({ Authorization: token() }) }),
  onSchemaStale: ({ client, server }) => console.warn(`Generated against ${client}; the server is on ${server}.`)
});
```

A request travels as a URL when it fits the server's budget and reads nothing sensitive, which lets a cache between the client and the server keep the answer, and as a body otherwise. The budget is learned from every response (`Scry-Url-Limit`). A URL refused before it reached the server (a 400, 404, 414 or 431 without that header) is asked again as a body, and nothing as long is asked as a URL again; a 405 turns the URL form off; a refusal saying the request must travel as a body is asked again as one. This is the C# client's behaviour.

Each answer is read by a plan the query builds alongside its projection, so every value arrives as its type says:

- a `long` as a `bigint` and a `decimal` as its exact text, read from the number's own text rather than a JavaScript number;
- bytes from base64 to a `Uint8Array`;
- an enum checked against the model, a value renamed since the client was generated mapped back through the aliases the response carries, and one with no name in the model failing as a stale client;
- each row keyed by the names the query wrote.

A refusal throws `ScryRequestError` with the server's status, `code` and message; `ScryPermissionError` and `ScryStaleClientError` are told apart. A body cut off part way, or an answer of another kind than the terminal asked for, throws `ScryWireError` rather than returning a short answer. `schemaStatus(scry)` says which stamps the client and the server are on.



## Streams, batches and live queries

```ts
for await (const row of scry.order.orderBy(_ => _.id).select(_ => ({ id: _.id, amount: _.amount })).stream()) {
  // Each row as it arrives.
}
```

`stream()` reads the rows a line at a time as the server writes them. A failure after the first row throws the error its status would have been, and a stream cut off before the server's closing marker throws `ScryWireError`, so a short read is never taken for the whole answer. Leaving the loop early closes the connection. A projection of binary members streams as binary parts, which this client does not read yet: it refuses such an answer rather than misreading it.

```ts
const batch = createBatch(scry);
const count = batch.add(_ => _.employee.count());
const names = batch.add(_ => _.employee.select(_ => ({ name: _.name })).toList());
await batch.send();
```

A batch sends its queries as one request. Each entry is answered as it would have been alone, so one refused entry rejects its own promise and the others resolve. A batch is sent once, and each entry is one query ended by its terminal.

```ts
const totals = scry.order.groupBy(_ => _.region).select(g => ({ region: g.key, total: g.sum(_ => _.amount) })).live();
for await (const answer of totals) {
  // The whole current answer, each time it changes.
}

const stop = scry.order.liveCount().subscribe(count => show(count), error => fail(error));
```

A live query is one request answered whenever its answer changes. `live()` answers with the rows, and `liveCount`, `liveLongCount`, `liveAny`, `liveFirstOrDefault` and `liveSingleOrDefault` with the scalar. A live query is an `AsyncIterable` that runs until the loop leaves it, and `subscribe` calls back until the function it returns is called.

A connection that ends is asked for again naming the last answer held, so an answer that has not moved is not sent again, and the consumer sees a pause rather than an end. The server failing for the moment, a server-side limit on open subscriptions, a 5xx, 408 or 429 from something in between, and a failed connection are all asked again. A rejection, a denial, or a client the server calls stale ends the live query with its error, since asking again would not change the answer. The C# client follows the same rules.

The `retry` option of `createScry` decides how long to wait before asking again. It is told how many attempts have failed since the last answer, how long that has been, and why the last one failed, and it returns milliseconds, or `undefined` to stop. The default, `backoff`, asks at once, then after a second, doubling to thirty, with a fifth either way at random so that clients cut off together do not return together.

## Commands

```ts
import { createCommands, resultOf } from "scry-client";
import type { Commands } from "./scry.generated.ts";

const commands = createCommands<Commands>(scry);

const hired = await commands.send.createEmployee({ name: "Ann", departmentId: 1, status: "FullTime" });
if (hired.status === "Completed") {
  show(`Hired #${hired.result.id}`);
}
```

`createCommands` takes the sources `createScry` made, and `send` holds one function per command the model declares, typed by its payload and its result. A payload is written as a query's constant is, so a date may be a `Date` or its text, and a [Flags] value its names. It travels camel-cased, each value in the JSON type the server binds it from: a `long` and a `decimal` as numbers, every enum by its name. A member the command does not have, or a null it cannot hold, is refused before anything is sent.

What comes back is an outcome:

| `status` | Means |
| --- | --- |
| `Completed` | Handled. `result` holds what the handler answered with, read as the model describes it. |
| `Failed` | Accepted and then not done: the handler refused or failed, or the target was gone by the time the command arrived. `error` is the reason the server showed. |
| `Pending` | Still being handled when the client stopped waiting. `completion` settles with the outcome, and meanwhile the command is listed in `commands.pending`. |
| `Unknown` | The connection it was answered on ended, and the server no longer holds its outcome or would not answer for it when asked again. It may have run. |

A refusal is not an outcome. A command the server would not accept (malformed, denied, one too many) never ran, so sending it throws, as a refused query does. Every outcome has a `completion`, which is the outcome itself where it is final and never rejects. `resultOf(outcome)` answers the result of a completed command and throws `ScryCommandFailedError` otherwise.

The server answers at once where it decides a command within its sync window, and otherwise answers pending and streams the outcome on the same response. The client waits `wait` milliseconds (three seconds unless set; `Infinity` waits for the outcome) before answering `Pending`. A pending command's connection that ends first is asked for again by the command's id, under the `retry` policy live queries use. `commands.pending` lists each command from going pending until its outcome has been seen: a completed one leaves after `completedLinger` milliseconds (eight seconds unless set), and a failed or unknown one stays until `clearFinished()`, since it says something somebody should read. `onPendingChanged` says when the list moves. `close()` stops following, and each command still followed ends `Unknown`; the server finishes what it accepted either way.

`commands.can("createEmployee")` says whether this caller may send the command at all. It is `false` until the server has answered, which the first call starts; `ready` settles once it has, and `onCapabilitiesChanged` says when the answer moves. A denial asks again. Each targeted command also adds a `can{Command}` member to its target, decided on the server from the command's policy, which a query reads as it reads any member: `scry.employee.select(_ => ({ name: _.name, deletable: _.canDeleteEmployee }))`. Both are advisory: the server decides again on every command.


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

Per command, `{Command}Command`, its payload, in which a nullable value may be left out, and the class its result is, by the server's name for it.


## Developing

Node 23.6 or later runs TypeScript as it is, so there is no build step.

```bash
npm ci
npm run check
```

`check` type-checks, which also runs the assertions in `test/types.ts`, and runs the tests. The tests read fixtures the .NET side keeps as snapshots in `src/Scry.Tests/TypeScript` — the wire vocabulary, the test model's description, the request corpus — so a change there reaches this client as a failing test.

`test/server.test.ts` runs against a running `Sample.WebServer` when `SCRY_SERVER` names it, generating the model from the server's own introspection; it is skipped otherwise.

```bash
SCRY_SERVER=http://localhost:5249 node --test test/server.test.ts
```

What is generated for the two fixture descriptions is committed under `test/generated`. After changing the generator, or after the .NET side accepts a new description, write it again and review the difference:

```bash
npm run fixtures
```
