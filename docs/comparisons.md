# Comparisons

Scry fills a narrow slot: a **single first-party client**, written in **C#**, that needs to shape its own **read** queries against a **server-owned EF Core model** — without the server hand-writing an endpoint per screen.

Most alternatives are broader than that, because they solve a harder problem: many consumers, many languages, writes, and a contract that has to outlive the team shipping it. That breadth is paid for in a second query language, a second type system, or a resolver layer. Where the breadth is needed, they are the right answer and Scry is not. This page is about telling the two situations apart.

Read [Intended use](../readme.md#intended-use) first. If those assumptions do not hold, most of what follows resolves to "use something else".


## At a glance

| | Scry | GraphQL | OData | Hand-written endpoints | gRPC |
| --- | --- | --- | --- | --- | --- |
| Query language | C# LINQ | GraphQL documents against an SDL schema | `$filter`/`$select`/`$expand` in a URL | none — the endpoint *is* the query | none — the method *is* the query |
| Client types come from | the server model dll, read by path at build time | schema introspection + codegen (StrawberryShake) | `$metadata` + connected-service codegen, or untyped | hand-written DTOs, or OpenAPI codegen | `.proto` codegen |
| Type systems to keep in sync | one (C#) | two (C# ↔ SDL) | two (C# ↔ EDM) | two (server DTO ↔ client DTO) | two (C# ↔ proto) |
| New query shape for a new screen | client-only | free if the fields exist, else a new field + resolver | free if the option is enabled | new endpoint + DTO + test + deploy | new method + messages |
| Exposure default | deny — opt in per type and member | the schema is the allow-list, by construction | the convention model builder exposes every property of a registered entity set | whatever the DTO carries | whatever the message carries |
| Reads / writes | read-only | queries, mutations, subscriptions | full CRUD | anything | anything |
| Non-.NET clients | no | yes | yes | yes | yes |
| Suits a public, multi-consumer contract | no | yes | yes | yes | yes |
| Per-field resolution | none — one translated EF query | resolver per field; needs DataLoader to avoid N+1 | none — one translated query | none | none |
| Cost control | fixed shape limits (page size, depth, pipeline length, `IN` values) | depth + complexity analysis, persisted queries | `[EnableQuery]` / `ODataQuerySettings` limits | inherent — the shape is fixed | inherent |

The row that matters most is **type systems to keep in sync**. Everything else on this page follows from Scry having one, and from that one being the language the UI is already written in.


## The same query, five ways

Active employees, ordered by name, with the manager and department names:

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

GraphQL, with Hot Chocolate's filtering and sorting conventions:

```graphql
query {
  employees(where: { active: { eq: true } }, order: [{ name: ASC }]) {
    name
    status
    manager { name }
    department { name }
  }
}
```

OData v4:

```
GET /odata/Employees
    ?$filter=Active eq true
    &$orderby=Name
    &$select=Name,Status
    &$expand=Manager($select=Name),Department($select=Name)
```

A hand-written endpoint — the query lives on the server, so the client only names it:

```
GET /api/employees/active?sort=name
```

gRPC — likewise, plus a generated request message:

```protobuf
rpc ListActiveEmployees (ListActiveEmployeesRequest) returns (EmployeeList);
```

The first three shape the query on the client. Only the first is checked by the C# compiler before it can be sent, and only the first names members that the server *generated* rather than members the client spelled.


## GraphQL

The largest overlap, and the most common alternative. Both let a client ask for a shape instead of calling a fixed endpoint, both re-check the request against a server-declared surface, and Hot Chocolate's `[UseFiltering]`/`[UseSorting]`/`[UseProjection]` translate onto the same `IQueryable` Scry does.

Where they differ:

- **One type system, not two.** GraphQL's SDL is a second type system that has to be mapped to and from C#, and a second codegen step on the client. Scry's client types are generated from the model dll itself, so a renamed property is a **compile error** in the UI rather than a runtime "field not found". See [Source generator](source-generator.md).
- **The query language is the host language.** The UI writes LINQ, in the file it already lives in, with IntelliSense and refactoring support that came free. GraphQL documents are strings that tooling has to be taught about.
- **Nothing to resolve.** A resolver per field is what lets GraphQL federate — and what makes DataLoader necessary. Scry's pipeline is one translated EF query, so there is no resolver layer to N+1 in.
- **Read-only, one model.** Mutations, subscriptions, federation, and stitching across back ends are all GraphQL and none of them Scry.
- **Evolution.** GraphQL is built to be a long-lived contract for consumers the publishing team does not deploy: deprecate, never break. Scry assumes client and server ship together, and instead carries a [schema stamp](schema-versioning.md) so a *stale* client is detected rather than tolerated.

**Choose GraphQL when** there is more than one consumer, any consumer is not .NET, the same graph also needs writes, or services need to federate.


## OData

The closest in mechanism — it is the other "LINQ over the wire" answer. `$filter`, `$orderby`, `$select`, `$top`, and `$skip` are parsed server-side into an expression tree over `IQueryable`, which is structurally what Scry's AST does. Scry's [paging design](paging.md) borrows from it directly.

Where they differ:

- **A URL string versus a captured expression tree.** OData queries are composed as text, by hand or by a query builder, and fail at runtime when they are wrong. Scry's LINQ is compiled first. The OData connected-service codegen does give typed entities from `$metadata`, but the filter surface is still assembled and translated by the client library at run time.
- **Exposure runs the other way.** `ODataConventionModelBuilder.EntitySet<T>()` exposes every property of the registered type, and narrowing happens from there. Scry's model is default-deny: a type is invisible without `[Queryable]`, and a member is invisible with `[QueryIgnore]`. See [Annotations](annotations.md) and [Security](security.md).
- **Result shape.** `$select`/`$expand` produce dynamic property bags that are awkward to consume in typed C#. Scry projects into the type the client's `Select` named.
- **Surface size.** OData is a large standard — `$apply`, `$compute`, `$batch`, functions and actions, delta links. Scry's vocabulary is deliberately small and closed so that every node can be individually validated and rebound; [LINQ coverage](linq-coverage.md) is a short page on purpose.

**Choose OData when** clients are cross-language, the URL contract itself is a deliverable, the ecosystem tooling that consumes OData feeds matters (Power BI, Excel Power Query), or writes are in scope.


## Hand-written endpoints (Web API, minimal APIs, BFF)

Still the default, and for a public API usually the correct one. The contract is explicit, readable in one file, and trivially auditable.

The cost is one endpoint, one DTO, one test, and one deploy per screen — concentrated exactly where a UI iterates fastest. The symptoms are familiar: over-fetching because the DTO serves three callers, under-fetching that turns into a second round trip, and the slow accretion of `?includeDepartment=true&sort=name&status=…` until the endpoint has grown a query language nobody designed.

Scry's trade is that the endpoint count stops growing, in exchange for a surface that has to be reasoned about as a whole rather than one endpoint at a time. Reading a controller is replaced by reviewing the allow-list — which is what the [review checklist](security.md#review-checklist) is for.

This is the one comparison that is **not** either/or. Scry is read-only, so commands, writes, and anything with server-side business rules stay ordinary endpoints. The realistic end state is hand-written endpoints for writes alongside Scry for reads.


## gRPC and contract-first RPC

Not really a competitor in kind. gRPC is a transport plus a contract-first RPC model: excellent codegen, efficient binary framing, streaming, and genuinely cross-language. But it shares the property that matters here with hand-written endpoints — **one method per use case** — so query shaping stays a server-side concern and the churn stays server-side too.

Worth noting that the two are not exclusive: Scry is not tied to HTTP and JSON. `ScryProcessor.Execute` is the single choke point for validation and execution, so a gRPC or SignalR method can carry a `QueryRequest` as readily as the mapped endpoint does. See [Hosting without the HTTP endpoint](server.md#hosting-without-the-http-endpoint).


## Expression-tree serializers and dynamic LINQ

Libraries that serialize `System.Linq.Expressions` trees, or parse a client-supplied string into one, look like the same idea: send LINQ to the server. The distinction is what actually crosses the wire.

A general expression serializer has to carry **type names and method names**, because the receiving side reconstructs real `Type` and `MethodInfo` from them. A string-based dynamic LINQ parser has the same property by another route. That is precisely the capability Scry's wire format refuses to have: there is no node for a type name and no node for an arbitrary method call, only a [closed operator set](wire-format.md#operators) and a [closed function enum](security.md#2-a-closed-ast). Unknown discriminators fail deserialization instead of being ignored.

The consequence is a trust boundary. These libraries are built for expressiveness between peers that trust each other; where they offer restriction hooks, the safety depends on configuring them correctly. Scry starts from the opposite assumption — the client is hostile, and the vocabulary is the control.

**Choose an expression serializer when** both ends are owned and mutually trusted — two services from the same team, an internal tool, a test harness. They are far more expressive and far less work than a closed AST.


## Hand-rolled filter DTOs

The other roll-your-own answer: a serializable criteria object, posted to an endpoint that rebuilds expressions from it by reflection. The object carries property names as strings, an operator enum, a value, and a sort column. Teams land here after concluding that expression trees cannot cross the wire. Structurally the object *is* a serialized query AST — a very small one — which is why it works at all.

The costs are the vocabulary and the strings. Coverage stops where the hand-written expression builder stops; each operator, type, and nesting level is another case to write. Property names are strings the compiler cannot check, so a rename fails at runtime. And each name arriving on the wire is a reach into the model, unbounded until an allow-list is added by hand.

Scry is this design carried to completion. The criteria language is [generated, typed LINQ](querying.md), the vocabulary is closed but broad, and validation is default-deny. The scenario the DTO exists for — criteria assembled at runtime from a filter UI — needs no DTO at all; see [composing a query at runtime](querying.md#composing-a-query-at-runtime).


## Generated APIs over the database (Hasura, PostgREST, Supabase)

These derive an API straight from the database schema and express authorization in the database — row-level security, or the tool's own policy engine. Nothing to write on the server at all.

The differences are structural: the source of truth is the database rather than the C# model, authorization lives outside the application and is versioned separately from it, and the client speaks GraphQL or REST rather than C#. Scry keeps the allow-list ([annotations](annotations.md)) and the row filters ([`IReturnablePolicy<T>`](policies.md)) in the same codebase and the same commit as the model they guard.

**Choose them when** no back-end code is wanted at all, or authorization is already expressed as row-level security in the database.


## No boundary at all (Blazor Server, MVC, Razor Pages)

If the UI runs on the server, none of this is needed. Inject the `DbContext` and write LINQ directly — no capture, no serialization, no validation, because there is no hostile boundary to cross.

Scry exists because a WebAssembly client is a separate process that an attacker controls. If Blazor Server is still on the table, it is the cheaper answer to the query problem, and the comparison worth making is Blazor Server versus WASM rather than Scry versus anything.


## A note on tRPC

Readers coming from TypeScript will spot the family resemblance to tRPC, and it is the intended one: one language on both sides, client types **derived** from the server rather than declared twice, and no schema language in between. Scry differs in what the client sends — a *query* the server validates and translates, rather than a call to a named procedure — but the motivation is the same, and so is the constraint that makes it work: both ends are one team's code, shipped together.


## When Scry is the wrong choice

- **The API is a public contract**, or serves consumers on a release cycle the team does not control. A generated client is bound to the surface it was generated against; that is a deliberate coupling, not an oversight.
- **Any client is not .NET.** There is no non-.NET client story, and a hand-written wire request is not one.
- **Writes are in scope.** Scry is read-only by design — see [Out of scope](linq-coverage.md#out-of-scope). Pair it with ordinary endpoints.
- **The read model is not EF Core.** Non-EF data can be surfaced as a [`[QueryablePoco]` source](server.md#poco-sources), but that runs the pipeline in memory over the supplied sequence — fine for lookup tables, not for a primary data path.
- **The query shapes are few and stable.** Four endpoints that rarely change are not a problem worth a query engine.
- **The queries need operators Scry does not carry.** The vocabulary is closed and covers most of what EF Core translates — joins, set operations, grouping, subqueries and collection aggregates included — but it is a fixed set rather than arbitrary LINQ, and it grows one audited addition at a time. Check [LINQ coverage](linq-coverage.md) against real queries before committing.
- **The UI runs on the server.** See above.


## Summary

Scry is not a smaller GraphQL or a typed OData. It is a narrower design, and a deliberate trade: give up cross-language reach, writes, and public-contract stability, and in return the query language, the client types, and the server model collapse into a single C# type system with a default-deny allow-list enforced at runtime.

That trade pays off for a Blazor WASM front end talking to its own back end, built by one team, deployed together. Outside that shape, one of the alternatives above is the better tool.
