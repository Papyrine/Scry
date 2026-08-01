# Scry documentation

Type-safe, serializable LINQ from a client to a server-side EF Core model.


## Guides

| Page | Contents |
| --- | --- |
| [Getting started](getting-started.md) | The three-project layout, wiring the model, server, and client end to end. |
| [Comparisons](comparisons.md) | Scry against GraphQL, OData, hand-written endpoints, gRPC, and expression-tree serializers — and when to pick one of those instead. |
| [Annotations](annotations.md) | `[Queryable]`, `[QueryableView]`, `[QueryablePoco]`, `[QueryIgnore]`, `[ReturnableWith]`, and what each exposes. |
| [Source generator](source-generator.md) | How the model assembly is read by path, the MSBuild wiring, what is emitted, and troubleshooting. |
| [Writing queries](querying.md) | The supported LINQ surface: operators, expressions, functions, projections, grouping, terminals. |
| [LINQ coverage](linq-coverage.md) | Scry vs the EF Core–translatable surface: what is supported, and why anything left out is left out. |
| [Paging](paging.md) | The `ToPageAsync` page envelope and limits (offset paging); the keyset-cursor design (slices 2–3, pending). |
| [Batching](batching.md) | Sending several queries as one request, and what stays per-entry. |
| [Server](server.md) | `AddScry`, `MapScry`, `ScryOptions`, limits, POCO sources, hosting without HTTP, error handling. |
| [Row policies](policies.md) | `IReturnablePolicy<T>` for tenant scoping, soft delete, and row-level security. |
| [Observability](observability.md) | The traces, metrics, and per-query audit hook the server emits. |
| [Security model](security.md) | The threat model, every enforcement layer, and what Scry does *not* protect. |
| [Wire format](wire-format.md) | The JSON query AST: request, response, every node type, versioning. |
| [Schema versioning](schema-versioning.md) | The wire version and schema stamp axes, and detecting a stale deployed client. |
| [Query explorer](explorer.md) | The opt-in browser-Roslyn explorer UI and the introspection endpoint. |
| [Sample](sample.md) | Running the bundled sample and what each part of it demonstrates. |


## How it works

1. The EF Core model lives **server-side**. The client never references it — it is *pointed at by path*.
2. A **source generator** in the client reads the model assembly directly by path (`System.Reflection.Metadata`), applies an **allow-list**, and generates strongly-typed client query DTOs plus a queryable entry point.
3. The UI writes ordinary **LINQ** against the generated types.
4. The LINQ is captured and **serialized to a restricted query AST**.
5. The server **deserializes, re-validates against the allow-list at runtime, rebinds to the real EF types, executes**, and returns the projected rows.

```
Sample.Model (EF Core + [Queryable])
   │
   │  built DLL, read as metadata via <ScryModelDll>     ┌──────────────────────────┐
   ├─────────────────────────────────────────────────────▶ Scry.SourceGenerator     │
   │                                                     │ EmployeeQueryModel.g.cs  │
   │                                                     │ ScryEnums.g.cs           │
   │                                                     │ ScryQuery.g.cs           │
   │                                                     └────────────┬─────────────┘
   │                                                                  │
   │                                                    Sample.Client (ordinary LINQ)
   │                                                                  │
   │                                          QueryRequest (JSON AST) │ POST /api/query
   │                                                                  ▼
   └─────────────────────────────────────────────────────▶ Sample.Server
      referenced normally                                  validate → policy → rebind
                                                           → EF Core → project → JSON
```


## Packages

| Package | Purpose |
| --- | --- |
| [Scry.Annotations](https://nuget.org/packages/Scry.Annotations/) | Allow-list attributes applied to the server model. |
| [Scry.Wire](https://nuget.org/packages/Scry.Wire/) | The serializable query AST shared by client and server. |
| [Scry.Client](https://nuget.org/packages/Scry.Client/) | Client-side `IQueryable` provider (no EF dependency). Ships the source generator. |
| [Scry.Server](https://nuget.org/packages/Scry.Server/) | Server-side validation + execution against EF Core. |
| [Scry.Server.Explorer](https://nuget.org/packages/Scry.Server.Explorer/) | Opt-in query explorer UI. |

`Scry.SourceGenerator` is not published on its own — it is packed inside `Scry.Client` as an analyzer, so referencing `Scry.Client` is all a client project needs.

Every package puts its public types in the single `Scry` namespace, so one `using Scry;` covers all of them. The generated query models are the exception — they land in `Scry.Generated`, which keeps names derived from the server model out of Scry's own API surface.

## Requirements

- .NET 10 (`net10.0`) for `Scry.Wire`, `Scry.Client`, `Scry.Server`, and `Scry.Server.Explorer`.
- `Scry.Annotations` targets `netstandard2.0`, so any model project can reference it.
- EF Core on the server. The client has no EF dependency, which keeps it small under trimmed Blazor WebAssembly.


## Editing these docs

The markdown under `/docs` has its code blocks **generated in place**. Edit the `.md` files directly, then build `src/Scry.Tests`, which runs [MarkdownSnippets](https://github.com/SimonCropp/MarkdownSnippets) to overwrite the content inside each `snippet` region with the current source. Prose outside those regions is authored by hand; only the snippet blocks are managed.

```bash
dotnet build src/Scry.Tests/Scry.Tests.csproj
```

Every code block sourced from the repo is pulled in with a `snippet: key` directive, where the key is defined in a real source file:

```cs
// begin-snippet: myKey
...code...
// end-snippet
```

That keeps the documentation compiling and tested alongside the code it describes.
