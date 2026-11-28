# Scry for TypeScript

The TypeScript client: a generator that writes typed models from a description of the server, and a client whose queries are the C# client's bytes for the same query. The package is private and unpublished.

[docs/typescript.md](../../docs/typescript.md) documents it: [generating the model](../../docs/typescript.md#generating-the-model), with or without a running server, [writing queries](../../docs/typescript.md#writing-queries), and [developing the client](../../docs/typescript.md#developing-the-client), which covers its tests, the fixtures the .NET side keeps for them, and the end-to-end run. Two samples run it against `Sample.WebServer`: `samples/Sample.TypeScriptClient` on Node, and `samples/Sample.TypeScriptWeb` in a browser.

```bash
npm ci
npm run check
```

`check` type-checks and runs the tests. Those that need a running server are skipped unless `SCRY_SERVER` names one.
