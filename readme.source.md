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
| [Scry.Annotations](https://nuget.org/packages/Scry.Annotations/) | Allow-list attributes applied to the server model. |
| [Scry.Wire](https://nuget.org/packages/Scry.Wire/) | The serializable query AST shared by client and server. |
| [Scry.Client](https://nuget.org/packages/Scry.Client/) | Client-side `IQueryable` provider (no EF dependency). Ships the source generator. |
| [Scry.Server](https://nuget.org/packages/Scry.Server/) | Server-side validation + execution against EF Core. |
| [Scry.Server.Explorer](https://nuget.org/packages/Scry.Server.Explorer/) | Opt-in, GraphiQL-style query explorer. |

`Scry.SourceGenerator` is packed inside `Scry.Client` rather than published separately.

## At a glance

Annotate the server model:

snippet: queryableEntity

Register and map on the server:

snippet: serverRegistration

`AddPocoSource` supplies the rows for a `[QueryablePoco]` type — see
[POCO sources](docs/server.md#poco-sources).

snippet: mapScry

Point the client at the model by path — no reference:

snippet: clientModelPath

Then write LINQ:

snippet: clientQuery

## Query explorer

An opt-in, GraphiQL-style explorer ships in `Scry.Server.Explorer`. It runs Roslyn in the browser, so
you get real IntelliSense and diagnostics against the allow-listed schema, and can see exactly what
goes on the wire:

```csharp
app.MapScryExplorer("/scry");
```

<img src="docs/images/explorer-run.png" border="1"
     alt="The Scry explorer: LINQ, the serialized wire request, the result table, and the raw response">


It is off unless mapped, and Development-only by default. See
[Query explorer](docs/explorer.md).

## Documentation

- [Getting started](docs/getting-started.md)
- [Annotations](docs/annotations.md)
- [Source generator](docs/source-generator.md)
- [Writing queries](docs/querying.md)
- [Server](docs/server.md)
- [Row policies](docs/policies.md)
- [Security model](docs/security.md)
- [Wire format](docs/wire-format.md)
- [Query explorer](docs/explorer.md)
- [Sample](docs/sample.md)

## License

Source is MIT. Binary releases are subject to the [Open Source Maintenance Fee](OsmfEula.txt).


## Icon

[Ripple](https://thenounproject.com/icon/ripple-2664516/) by [Zach Bogart](https://thenounproject.com/creator/zachbogart/) via [The Noun Project](https://thenounproject.com)
