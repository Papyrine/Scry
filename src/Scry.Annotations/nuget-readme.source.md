# Scry.Annotations

Allow-list attributes for [Scry](https://github.com/Papyrine/Scry). Apply them to a
server-side EF Core model to control which types and properties are exposed to client-side queries.

- `[Queryable]` — opt a table-backed entity into querying.
- `[QueryableView]` — opt a keyless EF view into querying.
- `[QueryablePoco]` — opt a non-persisted POCO into querying.
- `[QueryIgnore]` — exclude a property from an opted-in type.
- `[ReturnableWith(typeof(Policy))]` — attach a server-side row/instance policy.

snippet: queryableEntity

Nothing is exposed without an opt-in attribute, and the server re-validates every incoming query
against the same attributes at runtime.

Docs: [Annotations](https://github.com/Papyrine/Scry/blob/main/docs/annotations.md)
