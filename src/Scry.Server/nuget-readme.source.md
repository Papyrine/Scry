# Scry.Server

Server-side execution for [Scry](https://github.com/Papyrine/Scry). Validates an incoming
query AST against the allow-list, rebinds it to the real EF Core entity types, applies row-level
policies, executes against a `DbContext`, and returns projected rows.

snippet: serverRegistration

snippet: mapScry

Docs: [Server](https://github.com/Papyrine/Scry/blob/main/docs/server.md) ·
[Row policies](https://github.com/Papyrine/Scry/blob/main/docs/policies.md) ·
[Security model](https://github.com/Papyrine/Scry/blob/main/docs/security.md)
