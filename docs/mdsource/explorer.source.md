# Query explorer

`Scry.Server.Explorer` is an opt-in, GraphiQL-style query explorer. It serves a self-contained Blazor
WebAssembly UI that runs **Roslyn in the browser**: write strongly-typed C# LINQ against the
allow-listed sources with real IntelliSense and diagnostics, see the serialized wire request, execute
it, and inspect the results.

It is off unless mapped, and Development-only by default.

![The explorer after running a query: the LINQ, the serialized wire request, the result table, and the raw response](images/explorer-run.png)

One screen shows the whole pipeline: the LINQ you wrote, the wire request it translated to, the rows
the server returned, and the raw response envelope.

## Mapping it

```cs
app.MapScry("/api/query");
app.MapScryExplorer("/scry");
```

Then browse to `/scry`.

The explorer requires `AddScry` — it resolves the `ScryProcessor` to describe the schema.

## Options

snippet: explorerOptions

snippet: mapExplorer

| Option | Default | Meaning |
| --- | --- | --- |
| `Route` | `/scry` | Sub-path the UI is served under. |
| `QueryEndpoint` | `/api/query` | The `MapScry` endpoint the explorer POSTs to. Must match what you mapped. |
| `EnableGuard` | `DevelopmentOnly` | Decides, per request, whether the explorer is reachable. |

To expose it outside Development, replace the guard with your own check:

```cs
app.MapScryExplorer(options =>
{
    options.Route = "/scry";
    options.QueryEndpoint = "/api/query";
    options.EnableGuard = context => context.User.IsInRole("admin");
});
```

When the guard returns false every explorer route returns **404**, not 403 — a disabled explorer is
indistinguishable from one that was never mapped.

## Introspection

The UI reads the schema from `{Route}/introspect` on load. The same guard applies.

snippet: IntrospectionTests.Describe.verified.txt

The contract carries only what tooling needs: source names and kinds, the generated model names,
member names with the exact C# type spelling the source generator would emit, and the re-emitted
enums. It carries **no** policies, resolvers, connection details, or CLR internals.

Because `TypeDisplay` matches the generator's emission exactly, the explorer can synthesize an
identical set of query models in the browser, compile them with Roslyn, and offer completion against
the real allow-listed surface — which is what makes this real IntelliSense rather than a word list:

![Monaco's completion dropdown listing the allow-listed Employee members](images/explorer-intellisense.png)

Note what is offered and what is not: `Active`, `Department`, `Manager`, `Name`, `Status` — but no
`Salary`, because it is `[QueryIgnore]`d and therefore never reaches the introspection contract.

You can produce the same contract programmatically:

```cs
var introspection = processor.Describe();
```

## How it works

1. On load the UI fetches `{Route}/introspect`.
2. `ModelSynthesizer` turns that contract into the same C# the design-time generator would emit — the
   enums, one query model per type, and a `ScryQuery` facade.
3. `RoslynWorkspace` compiles that source in-browser and wraps the user's expression in a method body,
   so the C# completion service offers members, and diagnostics are real compiler diagnostics.
4. `SnippetExecutor` compiles the expression, runs it against a capturing client to build the LINQ
   expression tree, and calls the production `ToScryRequest` — so the wire request shown is produced
   by exactly the same translation the real client performs.
5. The request is POSTed to `QueryEndpoint`, where it is validated like any other.

A trailing terminal (`.ToListAsync()`, `.FirstAsync()`, `.CountAsync()`, or a plain `.ToList()`) is
recognised and folded into the wire request as its terminal operator.

Only validated requests reach the server. The explorer is a convenience over the same endpoint, not a
bypass of it — it cannot query anything a normal client could not.

## Deployment notes

The UI is published and embedded as manifest resources inside the `Scry.Server.Explorer` assembly, so
the package is fully self-contained: no static web assets manifest, no extra files to deploy, and
nothing to configure beyond the route.

Because the explorer reveals the complete queryable schema, leaving it mapped in production means
publishing that schema to anyone who passes the guard. The Development-only default is deliberate.

## Regenerating the screenshots

Unlike every other code block in these docs, the two images above are **not** merged from source at
build time — they are captured from a real browser and committed under `docs/images/`. They will
therefore drift silently when the explorer UI changes; refresh them when it does.

`ExplorerWalkthrough` in `samples/Sample.Tests/UiSnapshotTests.cs` drives the live explorer with
Playwright and writes the raw captures to a temp directory (it is `[Explicit]`, so it does not run in
a normal test pass, and the fixture is `[Category("Browser")]` so CI can opt out — pixel output is
environment sensitive):

```bash
dotnet test samples/Sample.Tests --filter "FullyQualifiedName~ExplorerWalkthrough"
```

It prints the output directory. `1-loaded.png`, `2-intellisense.png`, `3-run.png`, `3b-count.png`,
`4-hover.png`, and `5-dark.png` are captured; the two used here are `2-intellisense` and `3-run`,
trimmed of trailing whitespace and with the empty interior of the editor box spliced out.
