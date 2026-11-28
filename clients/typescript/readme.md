# Scry for TypeScript

The TypeScript client: a generator that writes typed models from a server's introspection, and a client whose queries are the C# client's bytes for the same query. [docs/typescript.md](../../docs/typescript.md) documents it, and `samples/Sample.TypeScriptClient` runs it against `Sample.WebServer`.

The package is private and unpublished.


## Developing

Node 23.6 or later runs TypeScript as it is, so there is no build step.

```bash
npm ci
npm run check
```

`check` type-checks, which also runs the assertions in `test/types.ts`, and runs the tests. The tests read fixtures the .NET side keeps as snapshots in `src/Scry.Tests/TypeScript` (the wire vocabulary, the test model's description, the request corpus, and the HTTP exchanges each endpoint answers with), so a change there reaches this client as a failing test.

`test/server.test.ts` runs against a running `Sample.WebServer` when `SCRY_SERVER` names it, generating the model from the server's own introspection; it is skipped otherwise.

```bash
SCRY_SERVER=http://localhost:5249 node --test test/server.test.ts
```

What is generated for the two fixture descriptions is committed under `test/generated`. After changing the generator, or after the .NET side accepts a new description, write it again and review the difference:

```bash
npm run fixtures
```
