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
<sup><a href='/samples/Sample.Model/Entities/Employee.cs#L3-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryableEntity' title='Start of snippet'>anchor</a></sup>
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
        // Department.Handbook is an [Attachment], and one exposed without a check is a startup
        // failure. Registered here rather than by [AttachmentWith] because the model project
        // references the annotations alone and has no server type to name.
        _.AddAttachmentPolicy<Department, HandbookPolicy>();
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
<sup><a href='/samples/Sample.Server/Program.cs#L31-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-serverRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddPocoSource` supplies the rows for a `[QueryablePoco]` type — see [POCO sources](docs/server.md#poco-sources).

<!-- snippet: mapScry -->
<a id='snippet-mapScry'></a>
```cs
app.MapScry("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L84-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L35-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientQuery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Query explorer

An opt-in, GraphiQL-style explorer ships in `Scry.Server.Explorer`. It runs Roslyn in the browser, giving real IntelliSense and diagnostics against the allow-listed schema, and shows exactly what goes on the wire:

```csharp
app.MapScryExplorer("/scry");
```

<img src="samples/Sample.Tests/UiScreenshotTests.ExplorerRun.verified.png" border="1" alt="The Scry explorer: LINQ, the serialized wire request, the result table, and the raw response">

It is off unless mapped, and Development-only by default. See [Query explorer](docs/explorer.md).

The client side has a companion: a [debug sidecar](docs/sidecar.md) that opens over the running app (<kbd>Alt</kbd>+<kbd>Q</kbd>) and shows every Scry exchange the page has made — decoded requests, pretty-printed responses, headers, and a one-click jump into the explorer with the captured query pre-populated.

<img src="samples/Sample.Tests/UiScreenshotTests.SampleSidecar.verified.png" border="1" alt="The sidecar open over the sample app: the captured exchanges, and one query's decoded request, response, and headers">


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
