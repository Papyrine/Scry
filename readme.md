# Scry

Type-safe, serializable LINQ from a client to a server-side EF Core model.

When a UI evolves quickly, server-side querying usually forces a choice between hand-coding a
bespoke endpoint and contract per use case, or adopting GraphQL/OData and shaping queries with a
separate query language. Scry removes that trade-off while keeping everything in C# and
strongly typed end to end:

1. The EF Core model lives **server-side**. The client never references it — it is *pointed at by path*.
2. A **source generator** in the client reads the model assembly directly by path
   (`System.Reflection.Metadata`), applies an **allow-list**, and generates strongly-typed client
   query DTOs plus a queryable entry point.
3. The UI writes ordinary **LINQ** against the generated types.
4. The LINQ is captured and **serialized to a restricted query AST**.
5. The server **deserializes, re-validates against the allow-list at runtime, rebinds to the real EF
   types, executes**, and returns the projected rows.

Add or extend a query by writing LINQ in the client — no new endpoint, no new contract — while the
server stays in full control of which types, properties, shapes, and rows can ever be returned.

## Packages

| Package | Purpose |
| --- | --- |
| `Scry.Annotations` | Allow-list attributes applied to the server model. |
| `Scry.Wire` | The serializable query AST shared by client and server. |
| `Scry.Client` | Client-side `IQueryable` provider (no EF dependency). |
| `Scry.Server` | Server-side validation + execution against EF Core. |
| `Scry.SourceGenerator` | Generates client query DTOs from the server model. |

## License

Source is MIT. Binary releases are subject to the [Open Source Maintenance Fee](OsmfEula.txt).
