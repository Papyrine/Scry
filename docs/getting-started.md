# Getting started

A Scry solution has three projects:

| Project | References | Role |
| --- | --- | --- |
| **Model** | `Scry.Annotations`, `Microsoft.EntityFrameworkCore` | The EF Core `DbContext` and entities, annotated with the allow-list attributes. |
| **Server** | `Scry.Server`, the model project | Hosts the `DbContext` and maps the query endpoint. |
| **Client** | `Scry.Client`, the model project *by path only* | Writes LINQ against the generated query models. |

The client does **not** reference the model. It points at the model's built DLL with an MSBuild property, and the source generator reads that file as metadata. Nothing from the model assembly is loaded, referenced, or executed on the client, and nothing but the allow-listed surface reaches generated code.


## 1. The model

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

`[Queryable]` opts the type in; nothing is exposed without it. Every public readable property is then exposed unless it carries `[QueryIgnore]`. See [Annotations](annotations.md) for views, POCOs, and the exact member rules.

The `DbContext` is an ordinary EF Core context — Scry needs no changes to it:

<!-- snippet: dbContext -->
<a id='snippet-dbContext'></a>
```cs
public DbSet<Department> Departments => Set<Department>();
public DbSet<Employee> Employees => Set<Employee>();
public DbSet<Order> Orders => Set<Order>();
public DbSet<EmployeeSummary> EmployeeSummaries => Set<EmployeeSummary>();
public DbSet<Asset> Assets => Set<Asset>();

protected override void OnModelCreating(ModelBuilder builder)
{
    builder.Entity<EmployeeSummary>()
        .HasNoKey()
        .ToView("EmployeeSummary");

    // Table-per-hierarchy: the derived types share the base table and are told apart by a
    // discriminator, which is what OfType narrows on.
    builder.Entity<Vehicle>();
    builder.Entity<Building>();
}
```
<sup><a href='/samples/Sample.Model/SampleContext.cs#L6-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-dbContext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The model project references `Scry.Annotations` alongside EF Core:

<!-- snippet: modelProjectReferences -->
<a id='snippet-modelProjectReferences'></a>
```csproj
<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
  <ProjectReference Include="..\..\src\Scry.Annotations\Scry.Annotations.csproj" />
</ItemGroup>
```
<sup><a href='/samples/Sample.Model/Sample.Model.csproj#L8-L14' title='Snippet source file'>snippet source</a> | <a href='#snippet-modelProjectReferences' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## 2. The server

Register the `DbContext` as usual, then register Scry against it:

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

`AddScry<TContext>` scans `typeof(TContext).Assembly` once at startup, builds the allow-list schema, and registers it as a singleton along with the `ScryProcessor`.

`AddPocoSource` is what supplies the rows for a `[QueryablePoco]` type, which has no table for the server to read — see [POCO sources](server.md#poco-sources) for the fixed and per-request forms.

Then map the endpoint:

<!-- snippet: mapScry -->
<a id='snippet-mapScry'></a>
```cs
app.MapScry("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L84-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That is a single HTTP endpoint which accepts a serialized query and returns the projected rows. It answers `POST`, where the query is the body, and `GET`, where the query [rides in the URL](wire-format.md#the-url-form) so the response can be cached and revalidated. See [Server](server.md) for all options, and [Row policies](policies.md) for row-level filtering.


## 3. The client

Point at the model DLL:

<!-- snippet: clientModelPath -->
<a id='snippet-clientModelPath'></a>
```csproj
<!-- The server model, pointed at by path. NOT referenced. -->
<ScryModelDll>$(MSBuildThisFileDirectory)..\Sample.Model\bin\$(Configuration)\net10.0\Sample.Model.dll</ScryModelDll>
```
<sup><a href='/samples/Sample.Client/Sample.Client.csproj#L7-L10' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientModelPath' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and add a build-ordering-only project reference:

```xml
<ItemGroup>
  <PackageReference Include="Scry.Client" />
  <!-- Build ordering only — NOT a real reference. -->
  <ProjectReference Include="..\Sample.Model\Sample.Model.csproj" ReferenceOutputAssembly="false" />
</ItemGroup>
```

The `Scry.Client` package brings the generator and the MSBuild targets that feed it the path, so those two lines are the whole setup. [Source generator](source-generator.md) covers what a project needs when it references `Scry.SourceGenerator` directly instead of via the package.

Register the client and the generated entry point:

<!-- snippet: clientRegistration -->
<a id='snippet-clientRegistration'></a>
```cs
builder.Services.AddHttpClient(
    "scry",
    _ => _.BaseAddress = new(builder.HostEnvironment.BaseAddress));
builder.Services.AddScryClient(
    "/api/query",
    _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
builder.Services.AddScoped<ScryQuery>();
```
<sup><a href='/samples/Sample.Client/Program.cs#L14-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddScryClient` registers a `ScryClient` that sends to the given endpoint using the `HttpClient` the delegate resolves — here a **named** one, so Scry's base address, and any handler pipeline it grows, stay separate from every other call the app makes. `ScryQuery` is generated into the `Scry.Generated` namespace.

The factory is reached through that delegate rather than being a dependency of `Scry.Client`, so an application that does not otherwise want `Microsoft.Extensions.Http` does not acquire it by referencing Scry.

A shorter overload takes whichever `HttpClient` the container holds:

<!-- snippet: clientWasmRegistration -->
<a id='snippet-clientWasmRegistration'></a>
```cs
services.AddScoped(
    _ => new HttpClient
    {
        BaseAddress = new("https://localhost")
    });
services.AddScryClient("/api/query");
services.AddScoped<ScryQuery>();
```
<sup><a href='/samples/Sample.Tests/ClientRegistrationTests.cs#L19-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientWasmRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That is a fair shortcut in WebAssembly, where the browser backs `HttpClient`, there is exactly one, and it already points at the app's own origin — so there is nothing for a name to disambiguate, and it saves the app a `Microsoft.Extensions.Http` reference it would otherwise carry into the browser. Prefer naming the client anywhere else: a bare `HttpClient` registration is discouraged outside WASM to begin with, and an ambient one may well belong to another API, which Scry would then quietly post to.

Either way the client is registered **scoped**, not transient. It records the schema stamp each response advertises and raises [`SchemaStaleDetected`](schema-versioning.md) at most once, so a fresh instance per injection would reset that and never report drift. That is also why a typed client (`AddHttpClient<ScryClient>`) is the wrong shape here: the factory registers those transient.


## 4. Write a query

Declare whatever shape the UI wants:

<!-- snippet: clientProjectionTypes -->
<a id='snippet-clientProjectionTypes'></a>
```cs
record EmployeeRow(string Name, Status Status, string? Manager, string Department);

record RegionSummary(string Region, decimal Total, int Count);
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L5-L9' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientProjectionTypes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

then write ordinary LINQ:

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

That query is captured — never executed client-side — serialized to the wire AST, sent, validated against the allow-list on the server, rebound to the real `Employee` type, run through EF Core, and returned as exactly the four projected columns.

Adding a new query is more LINQ. No new endpoint, no new contract, no server change.


## What comes for free

- `Salary` is `[QueryIgnore]`, so it is absent from `EmployeeQueryModel` — there is no way to write LINQ that references it. A hand-crafted request naming `Salary` is rejected with `400` by the server's independent validation pass.
- The `Status` enum is re-emitted client-side, so no model reference is needed to compare against `Status.FullTime`.
- Collection navigations (`Department.Employees`) are not exposed at all, so a client cannot fan a query out across a collection.

Next: [Writing queries](querying.md) for the full supported surface, or [Security model](security.md) for what is enforced and where.
