# Scry.Client

Client-side LINQ provider for [Scry](https://github.com/Papyrine/Scry). Write ordinary LINQ
against the source-generated query models; Scry captures it, serializes it to the query AST, and
sends it to the server. No EF dependency, so it stays small in a trimmed Blazor WebAssembly app.

This package also ships the Scry source generator, so a client project needs only a
`<ScryModelDll>` path to the server model's built DLL — never a reference to it.

snippet: clientModelPath

snippet: clientRegistration

snippet: clientQuery

Docs: [Getting started](https://github.com/Papyrine/Scry/blob/main/docs/getting-started.md) ·
[Writing queries](https://github.com/Papyrine/Scry/blob/main/docs/querying.md) ·
[Source generator](https://github.com/Papyrine/Scry/blob/main/docs/source-generator.md)
