# <img src="/src/icon.png" height="30px"> Scry

Type-safe, serializable LINQ from a client to a server-side EF Core model.

When a UI evolves quickly, server-side querying usually forces a choice between hand-coding a bespoke endpoint and contract per use case, or adopting GraphQL/OData and shaping queries with a separate query language. Scry removes that trade-off while keeping everything in C# and strongly typed end to end:

1. The EF Core model lives **server-side**. The client never references it — it is *pointed at by path*.
1. A **source generator** in the client reads the model assembly directly by path (`System.Reflection.Metadata`), applies an **allow-list**, and generates strongly-typed client query DTOs plus a queryable entry point.
1. The UI writes ordinary **LINQ** against the generated types.
1. The LINQ is captured and **serialized to a restricted query AST**.
1. The server **deserializes, re-validates against the allow-list at runtime, rebinds to the real EF types, executes**, and returns the projected rows.

Add or extend a query by writing LINQ in the client — no new endpoint, no new contract — while the server stays in full control of which types, properties, shapes, and rows can ever be returned.


## Intended use

Scry is designed for a **WebAssembly front end** — typically Blazor WASM — talking to its own back end. The client has no EF dependency, so it stays small under a trimmed WASM publish, while remaining strongly typed against the server's EF Core model.

It also assumes the front end and the back end are built by the **same team** and deployed together. A generated client is bound to the model surface it was generated against, and the two are expected to move in lockstep. Scry is deliberately *not* a general-purpose web API: it is not intended as a stable public contract for multiple external consumers, third-party apps, or clients on release cycles the team does not control. See [docs/schema-versioning.md](docs/schema-versioning.md) for how drift between the two is detected and mitigated.

"Same team" is about coupling, not trust. The client is still treated as hostile — the generated code, the LINQ, and the wire request are all attacker-controlled — and every guarantee is re-enforced server-side at runtime. See [docs/security.md](docs/security.md).


## Compared to other approaches

Ways for a client to query a server differ on two axes. The first is **where the query is shaped**: on the server, as an endpoint or method per use case that the client names, or on the client, as a query the server checks and runs. The second is **how many type systems describe the data**: two, when a contract or schema sits between the sides and each side is generated from it or mapped onto it, or one, when the client's types are derived from the server's.

| | Query shaped on the server | Query shaped on the client |
| --- | --- | --- |
| **Two type systems** — a contract or schema between the sides | An endpoint or method per use case, with DTOs written twice or generated from the contract | A query language of its own against a schema in its own definition language, or query options in a URL parsed onto the model |
| **One type system** — client types derived from the server's | A procedure per use case, with the client's types inferred from or shared with the server's code | **Scry** — and, for peers that trust each other, a serialized expression tree over a shared model assembly |

Scry is the cell where the query is written on the client, the compiler checking it is the one the UI already uses, and the client is still not trusted. What follows from that, against each type of approach:

| | Scry | An endpoint per use case | A query language of its own | Query options in the URL | Serialized expression trees | An API generated from the database |
| --- | --- | --- | --- | --- | --- | --- |
| Query written in | the host language, in the UI's own files | nothing — the endpoint *is* the query | a second language, as a document | URL text | the host language, or a string dialect of it | the tool's language, derived from the tables |
| Client types come from | the server model dll, read by path at build time | hand-written DTOs, or codegen from a contract | codegen from the schema | codegen from published metadata, or untyped | referencing the server's own assembly | codegen from the database schema |
| Type systems to keep in sync | one | two | two | two | one, by sharing the model | two |
| A wrong query fails | at compile time, in the UI | on the server, at compile time — the client only names it | at run time, unless tooling is taught the language | at run time | at compile time for a typed tree, at run time for a string | at run time |
| Crosses the wire as | a closed AST — a fixed vocabulary of operators, member paths, and constants | a use-case name and its parameters | a query document | a URL | type names and method names, reconstructed on the server | a query document or a URL |
| Exposure default | deny — opt in per type and member | whatever the DTO carries | the schema is the allow-list, by construction | every property of a registered type | everything the shared types reach | the exposed tables, narrowed by database policy |
| A new shape for a new screen | client-only | new endpoint, DTO, test, deploy | free if the fields exist, else a new field and resolver | free if the option is enabled | client-only | client-only |
| Guards against a hostile client | re-validation against the allow-list plus fixed shape limits, server-side at run time | inherent — the server wrote the query | the schema, plus depth and complexity analysis | server-side query settings | none by design — built for peers that trust each other | database policy |
| Server work per query | one translated query | one hand-written query | a resolver per field, batched to avoid N+1 | one translated query | one query, rebuilt from the tree | one generated query |
| Reads and writes | reads only | anything | both | both | anything | both |
| Clients outside .NET, many consumers, a public contract | no | yes | yes | yes | no | yes |

Most of the Scry column is one fact seen from different sides: there is one type system, and it is the one the UI is written in.

Two more sit outside the table:

- **A hand-rolled criteria object** — property names as strings, an operator enum, a value, a sort column — is the client-shaped cell built by hand, and structurally a small serialized query AST. Its vocabulary stops where the reflection code rebuilding expressions stops, its names are strings the compiler cannot check, and its allow-list has to be added afterwards. Scry is that design carried to completion.
- **No boundary at all.** If the UI runs on the server, inject the context and write LINQ against it directly. Scry exists because a WebAssembly client is a separate process that an attacker controls.

[Comparisons](docs/comparisons.md) makes the same comparison by name, writes the same query each way, and lists [when Scry is the wrong choice](docs/comparisons.md#when-scry-is-the-wrong-choice).


## How it works

The build-time and runtime flows are deliberately independent. Nothing is referenced across the client/server boundary: the only things that cross it are the model dll *by path* (build time) and the serialized wire AST (run time).


### Build time — generating the client

The source generator reads the EF model assembly *by path* and emits strongly-typed client query types from the allow-listed surface only. The assembly is never referenced, loaded, or executed.

```mermaid
flowchart TB
    subgraph model["Server model"]
        EF["EF model<br/>+ Scry.Annotations<br/>([Queryable], [QueryIgnore], …)"]
        DLL["Model dll"]
        EF --> DLL
    end

    subgraph client["Client (no EF dependency)"]
        GEN["Source generator<br/>reads dll via<br/>System.Reflection.Metadata"]
        GENTYPES["Generated query types<br/>(Scry.Generated)"]
        GEN --> GENTYPES
    end

    DLL -. "by path, never referenced" .-> GEN
```


### Run time — a query round-trip

The client's LINQ is captured (never executed client-side) and serialized to a restricted AST. The server re-validates that AST against the allow-list — to completion, before anything is respond — then rebinds to the real EF types, executes, and returns only the projected rows. A `byte[]` member marked `[BinaryTransfer]` skips base64 and travels as a raw multipart part beside the JSON ([binary transfer](docs/wire-format.md#binary-transfer)); one marked `[Attachment]` is not carried at all, and is fetched on demand by row key through a check of its own ([attachments](docs/attachments.md)).

```mermaid
flowchart TB
    subgraph client["Client"]
        LINQ["UI writes linq<br/>against generated types"]
        CAPTURE["QueryProvider<br/>captures expression tree<br/>(never executed here)"]
        TRANS["QueryTranslator<br/>→ restricted query AST"]
        LINQ --> CAPTURE --> TRANS
    end

    subgraph wire["Scry.Wire"]
        REQ["QueryRequest AST<br/>(closed operator + node set)"]
    end

    subgraph server["Server"]
        SCHEMA["Schema.Build<br/>allow-list from the real model"]
        VALID["QueryValidator<br/>authoritative gate<br/>(runs to completion first)"]
        BUILD["ExpressionBuilder<br/>rebind to real EF types"]
        EXEC["QueryExecutor + ProjectionPlan<br/>execute + shape rows"]
        DB[("EF → DB")]
        RESP["QueryResponse"]
        SCHEMA -. "allow-list" .-> VALID
        VALID --> BUILD --> EXEC --> DB
        DB -- "projected rows" --> RESP
    end

    TRANS -- "serialize + send" --> REQ
    REQ -- "deserialize" --> VALID
    RESP -- "rows" --> LINQ
```

See [docs/security.md](docs/security.md) for the full threat model.


## Packages

| Package | Purpose |
| --- | --- |
| [Scry.Annotations](https://nuget.org/packages/Scry.Annotations/) | Allow-list attributes applied to the server model. |
| [Scry.Wire](https://nuget.org/packages/Scry.Wire/) | The serializable query AST shared by client and server. |
| [Scry.Client](https://nuget.org/packages/Scry.Client/) | Client-side `IQueryable` provider (no EF dependency). Ships the source generator. |
| [Scry.Server](https://nuget.org/packages/Scry.Server/) | Server-side validation + execution against EF Core. |
| [Scry.Server.Explorer](https://nuget.org/packages/Scry.Server.Explorer/) | Opt-in, GraphiQL-style query explorer. |
| [Scry.Server.Delta](https://nuget.org/packages/Scry.Server.Delta/) | Opt-in `304 Not Modified`, backed by Delta. |

`Scry.SourceGenerator` is packed inside `Scry.Client` rather than published separately.

Every package puts its public types in the single `Scry` namespace, so one `using Scry;` covers all of them. The generated query models are the exception — they land in `Scry.Generated`.


## At a glance

Annotate the server model:

<!-- snippet: queryableEntity -->
<a id='snippet-queryableEntity'></a>
```cs
[Queryable]
public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Status Status { get; set; }
    public bool Active { get; set; }
    public DateOnly Created { get; set; }

    public int? ManagerId { get; set; }
    public Employee? Manager { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    // A claim check rather than a value: no query reads it, and what a client gets back is a handle
    // carrying this row's key. A photo is the case the attribute exists for — bytes nothing wants on
    // every row of every query, fetched by the one thing that actually wants to draw them. The check
    // that authorizes the fetch is registered by the server; this project references the annotations
    // alone, so [AttachmentWith] has no policy type to name here.
    [Attachment(ContentType = "image/svg+xml")]
    public byte[]? Photo { get; set; }

    // Never exposed to clients.
    [QueryIgnore]
    public decimal Salary { get; set; }

    // The other half of that pair: queryable, but never in a URL and never in a cache. [QueryIgnore]
    // hides a member outright; [Sensitive] keeps it askable while refusing the two ways its value
    // escapes — a query comparing it against a constant travels as a body rather than a URL, where the
    // constant would land in every access log on the way, and a response projecting it is sent
    // no-store, where a cacheable one would be written to the caller's disk.
    [Sensitive]
    public string Password { get; set; } = "";
}
```
<sup><a href='/samples/Sample.Model/Entities/Employee.cs#L3-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryableEntity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Register and map on the server:

<!-- snippet: serverRegistration -->
<a id='snippet-serverRegistration'></a>
```cs
builder.Services
    .AddScry<SampleContext>(
    _ =>
    {
        // Holiday is a [QueryablePoco]: it has no table, so the server supplies its rows. Every
        // [QueryablePoco] type must be registered here or AddScry throws at startup.
        _.AddPocoSource(_ => Holiday.Seed());
        // Department.Handbook and Employee.Photo are [Attachment]s, and one exposed without a
        // check is a startup failure. Registered here rather than by [AttachmentWith] because
        // the model project references the annotations alone and has no server type to name.
        _.AddAttachmentPolicy<Department, HandbookPolicy>();
        _.AddAttachmentPolicy<Employee, PhotoPolicy>();
        _.MaxPageSize = 200;

        // A row policy whose decision is too slow to run per row in SQL, so it runs in C# and
        // the server remembers what it answered. Revision is what tells it a row has changed
        // and needs deciding again — see /docs/policies.md and the /permissions page.
        _.AddCachedPolicy<Order, long, RegionAccessPolicy>(_ => _.Revision);

        // Repeat a query while nothing has been written and the answer is a 304 rather than a
        // re-execution. Optional, and off until a freshness source says how to tell — see
        // /docs/caching.md.
        _.UseDeltaFreshness<SampleContext>();

        // What a cached response belongs to. This server has sources whose answers depend on
        // who asked — the row policy above, and Department.Handbook's attachment check — and
        // MapScry refuses to start without this. The sample has no sign-in, so the caller
        // half is a constant; a real app returns its tenant or its principal, and a client
        // signing in as someone else is then never handed the previous one's rows.
        //
        // The grants version is the other half, and is the part worth copying. A response
        // varies by what the caller is allowed to see, and QueryFreshness only watches the
        // database — so a grant changing outside it would move nothing, and a cache holding
        // the old rows would go on answering with rows the caller has since lost.
        _.CacheScope = _ => $"sample-{_.RequestServices.GetRequiredService<RegionGrants>().Version}";
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L31-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-serverRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddPocoSource` supplies the rows for a `[QueryablePoco]` type — see [POCO sources](docs/server.md#poco-sources).

<!-- snippet: mapScry -->
<a id='snippet-mapScry'></a>
```cs
app.MapScry("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L85-L87' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Point the client at the model by path — no reference:

<!-- snippet: clientModelPath -->
<a id='snippet-clientModelPath'></a>
```csproj
<!-- The server model, pointed at by path. NOT referenced. -->
<ScryModelDll>$(MSBuildThisFileDirectory)..\Sample.Model\bin\$(Configuration)\net10.0\Sample.Model.dll</ScryModelDll>
```
<sup><a href='/samples/Sample.Client/Sample.Client.csproj#L7-L10' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientModelPath' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then write LINQ:

<!-- snippet: clientQuery -->
<a id='snippet-clientQuery'></a>
```cs
employees = await Query
    .Employee
    .Where(_ => _.Active)
    .OrderBy(_ => _.Name)
    .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
    .ToListAsync();
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L48-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientQuery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Query explorer

An opt-in, GraphiQL-style explorer ships in `Scry.Server.Explorer`. It runs Roslyn in the browser, giving real IntelliSense and diagnostics against the allow-listed schema, and shows exactly what goes on the wire:

```csharp
app.MapScryExplorer("/scry");
```

<img src="samples/Sample.Tests/UiScreenshotTests.ExplorerRun.verified.png" border="1" alt="The Scry explorer: the schema pane, the LINQ, the wire request it translated to, and the rows the server returned">

It is off unless mapped, and Development-only by default. See [Query explorer](docs/explorer.md).

The client side has a companion: a [debug sidecar](docs/sidecar.md) that opens over the running app (<kbd>Alt</kbd>+<kbd>Q</kbd>) and shows every Scry exchange the page has made — decoded requests, pretty-printed responses, headers, and a one-click jump into the explorer with the captured query pre-populated.

<img src="samples/Sample.Tests/UiScreenshotTests.SampleSidecar.verified.png" border="1" alt="The sidecar open over the sample app: the captured exchanges, queries and attachment fetches alike, and one query's decoded request, response, and headers">


## Documentation

- [Getting started](docs/getting-started.md)
- [Comparisons](docs/comparisons.md)
- [Annotations](docs/annotations.md)
- [Source generator](docs/source-generator.md)
- [Writing queries](docs/querying.md)
- [Server](docs/server.md)
- [Row policies](docs/policies.md)
- [Attachments](docs/attachments.md)
- [Batching](docs/batching.md)
- [Observability](docs/observability.md)
- [Caching and 304](docs/caching.md)
- [Performance](docs/performance.md)
- [Security model](docs/security.md)
- [Wire format](docs/wire-format.md)
- [Schema versioning](docs/schema-versioning.md)
- [Query explorer](docs/explorer.md)
- [Debug sidecar](docs/sidecar.md)
- [Sample](docs/sample.md)


## Icon

[Ripple](https://thenounproject.com/icon/ripple-2664516/) by [Zach Bogart](https://thenounproject.com/creator/zachbogart/) via [The Noun Project](https://thenounproject.com)
