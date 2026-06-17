# Skry.Annotations

Allow-list attributes for [Skry](https://github.com/Papyrine/Skry). Apply them to a
server-side EF Core model to control which types and properties are exposed to client-side queries.

- `[Queryable]` — opt a table-backed entity into querying.
- `[QueryableView]` — opt a keyless EF view into querying.
- `[QueryablePoco]` — opt a non-persisted POCO into querying.
- `[QueryIgnore]` — exclude a property from an opted-in type.
- `[ReturnableWith(typeof(Policy))]` — attach a server-side row/instance policy.
