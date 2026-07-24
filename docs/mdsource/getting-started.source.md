# Getting started

A Scry solution has three projects:

| Project | References | Role |
| --- | --- | --- |
| **Model** | `Scry.Annotations`, `Microsoft.EntityFrameworkCore` | The EF Core `DbContext` and entities, annotated with the allow-list attributes. |
| **Server** | `Scry.Server`, the model project | Hosts the `DbContext` and maps the query endpoint. |
| **Client** | `Scry.Client`, the model project *by path only* | Writes LINQ against the generated query models. |

The client does **not** reference the model. It points at the model's built DLL with an MSBuild
property, and the source generator reads that file as metadata. Nothing from the model assembly is
loaded, referenced, or executed on the client, and nothing but the allow-listed surface reaches
generated code.

## 1. The model

snippet: queryableEntity

`[Queryable]` opts the type in; nothing is exposed without it. Every public readable property is then
exposed unless it carries `[QueryIgnore]`. See [Annotations](annotations.md) for views, POCOs, and
the exact member rules.

The `DbContext` is an ordinary EF Core context — Scry needs no changes to it:

snippet: dbContext

The model project references `Scry.Annotations` alongside EF Core:

snippet: modelProjectReferences

## 2. The server

Register the `DbContext` as usual, then register Scry against it:

snippet: serverRegistration

`AddScry<TContext>` scans `typeof(TContext).Assembly` once at startup, builds the allow-list schema,
and registers it as a singleton along with the `ScryProcessor`.

Then map the endpoint:

snippet: mapScry

That is a single HTTP `POST` endpoint which accepts a serialized query and returns the projected
rows. See [Server](server.md) for all options, and [Row policies](policies.md) for row-level
filtering.

## 3. The client

Point at the model DLL:

snippet: clientModelPath

and add a build-ordering-only project reference:

```xml
<ItemGroup>
  <PackageReference Include="Scry.Client" />
  <!-- Build ordering only — NOT a real reference. -->
  <ProjectReference Include="..\Sample.Model\Sample.Model.csproj" ReferenceOutputAssembly="false" />
</ItemGroup>
```

The `Scry.Client` package brings the generator and the MSBuild targets that feed it the path, so
those two lines are the whole setup. [Source generator](source-generator.md) covers what a project
needs when it references `Scry.SourceGenerator` directly instead of via the package.

Register the client and the generated entry point:

snippet: clientRegistration

`AddScryClient` registers a `ScryClient` that POSTs to the given endpoint using the registered
`HttpClient`. `ScryQuery` is generated into the `Scry.Generated` namespace.

## 4. Write a query

Declare whatever shape the UI wants:

snippet: clientProjectionTypes

then write ordinary LINQ:

snippet: clientQuery

That query is captured — never executed client-side — serialized to the wire AST, POSTed, validated
against the allow-list on the server, rebound to the real `Employee` type, run through EF Core, and
returned as exactly the four projected columns.

Adding a new query is just more LINQ. No new endpoint, no new contract, no server change.

## What you get for free

- `Salary` is `[QueryIgnore]`, so it is absent from `EmployeeQueryModel` — there is no way to write
  LINQ that references it. A hand-crafted request naming `Salary` is rejected with `400` by the
  server's independent validation pass.
- The `Status` enum is re-emitted client-side, so no model reference is needed to compare against
  `Status.FullTime`.
- Collection navigations (`Department.Employees`) are not exposed at all, so a client cannot fan a
  query out across a collection.

Next: [Writing queries](querying.md) for the full supported surface, or
[Security model](security.md) for what is enforced and where.
