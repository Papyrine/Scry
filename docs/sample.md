# Sample

`/samples` contains a complete, runnable three-project solution: `Scry.Samples.slnx`.

| Project | Role |
| --- | --- |
| `Sample.Model` | EF Core model with the allow-list attributes. Referenced by the server, pointed at by path from the client. |
| `Sample.Server` | ASP.NET Core host: `DbContext`, `MapScry`, `MapScryExplorer`, and the Blazor host page. |
| `Sample.Client` | Blazor WebAssembly UI that writes LINQ against the generated models. |
| `Sample.Tests` | Snapshot tests over the rendered UI, the wire traffic, and the explorer endpoint. |


## Running it

The sample uses SQL Server LocalDB and creates/seeds the database on startup.

```bash
dotnet run --project samples/Sample.Server
```

Then browse to the URL it prints. The query explorer is at `/scry`.


## The model

Four sources covering each kind:

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

`Salary` is `[QueryIgnore]`d, so it is absent from the generated client model and rejected if named in a raw request.

<!-- snippet: queryableOrder -->
<a id='snippet-queryableOrder'></a>
```cs
[Queryable]
public class Order
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public decimal Amount { get; set; }

    // A collection of values, which EF stores as a JSON column. Present in the sample so the round-trip
    // tests cover one end to end: the generator spells its element from the model DLL and the server
    // spells it from reflection, and the two stamps only agree if they agree about this member.
    [QueryableCollection]
    public List<string> Tags { get; set; } = [];

    // What the cached row policy on this type reads to know a row needs deciding again. Server-side
    // machinery rather than query surface, so it is hidden from clients like anything else Scry was
    // not told to expose — a version column need not be one a client can see. A real deployment more
    // often maps a rowversion as ulong and lets the database move it; this one writes it, so the
    // sample can show a row being re-decided on demand.
    [QueryIgnore]
    public long Revision { get; set; }
}
```
<sup><a href='/samples/Sample.Model/Entities/Order.cs#L3-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryableOrder' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: queryableView -->
<a id='snippet-queryableView'></a>
```cs
/// <summary>A keyless EF Core entity mapped to a database view.</summary>
[QueryableView]
public class EmployeeSummary
{
    public string Department { get; set; } = "";
    public int Headcount { get; set; }
}
```
<sup><a href='/samples/Sample.Model/Entities/EmployeeSummary.cs#L3-L11' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryableView' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: queryablePoco -->
<a id='snippet-queryablePoco'></a>
```cs
/// <summary>A POCO that is not part of the persisted model.</summary>
[QueryablePoco]
public class Holiday
{
    public string Name { get; set; } = "";
    public DateOnly Date { get; set; }

    public static IEnumerable<Holiday> Seed() =>
    [
        new()
        {
            Name = "New Year",
            Date = new(2026, 1, 1)
        },
        new()
        {
            Name = "Workers Day",
            Date = new(2026, 5, 1)
        },
        new()
        {
            Name = "Christmas",
            Date = new(2026, 12, 25)
        }
    ];
}
```
<sup><a href='/samples/Sample.Model/Entities/Holiday.cs#L3-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryablePoco' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`EmployeeSummary` is a keyless entity over a database view, created by the seed routine.

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

## The server

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
        // MapScry refuses to start without this. The sample has no sign-in, so there is one
        // caller and one scope; a real app returns its tenant or its principal, and a client
        // signing in as someone else is then never handed the previous one's rows.
        _.CacheScope = _ => "sample";
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L31-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-serverRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Holiday` has no table, so its data is registered explicitly — see [POCO sources](server.md#poco-sources). `MaxPageSize` is lowered from the default 1000 to 200.

<!-- snippet: mapScry -->
<a id='snippet-mapScry'></a>
```cs
app.MapScry("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L79-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: mapExplorer -->
<a id='snippet-mapExplorer'></a>
```cs
app.MapScryExplorer(
    _ =>
    {
        _.Route = "/scry";
        // This sample always exposes the explorer. The default guard is Development-only — in a real
        // app, run in Development or set EnableGuard to your own check (e.g. an admin authorization).
        _.EnableGuard = _ => true;
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L119-L128' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapExplorer' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The sample always exposes the explorer so it can be browsed without setting an environment. A real app should leave the default Development-only guard in place, or replace it with an authorization check — see [Query explorer](explorer.md).

It also answers a repeated query with `304 Not Modified`, using [Delta](https://github.com/SimonCropp/Delta) as the freshness source behind the ETag — two settings inside `AddScry`, shown in the registration above.

The client half — re-asking with `If-None-Match` and replaying what the 304 stands for — is a `DelegatingHandler` in `Sample.Client`. Neither half is part of Scry; both are explained in [Caching and 304](caching.md).


## The client

<!-- snippet: clientModelPath -->
<a id='snippet-clientModelPath'></a>
```csproj
<!-- The server model, pointed at by path. NOT referenced. -->
<ScryModelDll>$(MSBuildThisFileDirectory)..\Sample.Model\bin\$(Configuration)\net10.0\Sample.Model.dll</ScryModelDll>
```
<sup><a href='/samples/Sample.Client/Sample.Client.csproj#L7-L10' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientModelPath' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: clientGeneratorWiring -->
<a id='snippet-clientGeneratorWiring'></a>
```csproj
<ItemGroup>
  <ProjectReference Include="..\..\src\Scry.SourceGenerator\Scry.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  <ProjectReference Include="..\Sample.Model\Sample.Model.csproj" ReferenceOutputAssembly="false" />
  <CompilerVisibleProperty Include="ScryModelDll" />
  <CompilerVisibleProperty Include="ScryModelStamp" />
</ItemGroup>

<Target Name="ComputeScryStamp"
        AfterTargets="ResolveProjectReferences"
        BeforeTargets="GenerateMSBuildEditorConfig;CoreCompile"
        Condition="Exists('$(ScryModelDll)')">
  <GetFileHash Files="$(ScryModelDll)" Algorithm="SHA256">
    <Output TaskParameter="Hash" PropertyName="ScryModelStamp" />
  </GetFileHash>
</Target>
```
<sup><a href='/samples/Sample.Client/Sample.Client.csproj#L24-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientGeneratorWiring' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Because the sample uses project references rather than the NuGet package, the generator wiring that `Scry.Client`'s `buildTransitive` props would normally supply is written out explicitly. See [Source generator](source-generator.md).

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


## The queries

The UI declares whatever shapes it wants:

<!-- snippet: clientProjectionTypes -->
<a id='snippet-clientProjectionTypes'></a>
```cs
record EmployeeRow(string Name, Status Status, string? Manager, string Department);

record RegionSummary(string Region, decimal Total, int Count);
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L5-L9' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientProjectionTypes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A filter, an ordering, and a projection that reaches through two navigations:

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

A group-by with aggregates:

<!-- snippet: clientGroupBy -->
<a id='snippet-clientGroupBy'></a>
```cs
regions = await Query
    .Order
    .GroupBy(_ => _.Region)
    .Select(_ => new RegionSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
    .ToListAsync();
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L44-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientGroupBy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And a query parameterized by closure-captured locals — the values are evaluated client-side and sent as constants, which is how an app builds a filtered query at runtime:

<!-- snippet: clientClosureCapture -->
<a id='snippet-clientClosureCapture'></a>
```cs
fullTimers = await Query
    .Employee
    .Where(_ => _.Status == status)
    .OrderBy(_ => _.Name)
    .Take(top)
    .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
    .ToListAsync();
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L52-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientClosureCapture' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## A policy too expensive to run per row

`Order` is scoped by a [cached row policy](policies.md#when-the-decision-is-too-expensive-for-sql). Which regions a caller may see is a lookup against another system — the sample's `RegionGrants`, standing in for a permissions service — and it sleeps for 25ms to make the point that it is not something a query can afford per row.

<!-- snippet: cachedRowPolicy -->
<a id='snippet-cachedRowPolicy'></a>
```cs
/// <summary>
/// Scopes <see cref="Order"/> to the regions the caller is granted. Written as a cached policy rather
/// than an ordinary <c>IReturnablePolicy</c> because the decision is a lookup against another system —
/// far too slow to run per row inside every query, and unchanging often enough to be worth remembering.
/// </summary>
public sealed class RegionAccessPolicy(RegionGrants grants) :
    ICachedRowPolicy<Order>
{
    /// <summary>
    /// Which set of answers this call belongs to. The sample has no sign-in, so there is one caller and
    /// one scope, exactly as <c>CacheScope</c> has one; a real app returns the tenant or the principal
    /// resolved from <c>context.Services</c>. Never from a request header — decisions are remembered
    /// per scope, so a caller choosing its own scope key is a caller choosing its own permissions.
    /// </summary>
    public string ScopeKey(ScryPolicyContext context) => "sample";

    /// <summary>
    /// The expensive part. It runs off the query path — for a row that is new, one whose
    /// <see cref="Order.Revision"/> has moved, and every row the first time a scope is read — and never
    /// again just because a query ran.
    /// </summary>
    public bool Allow(Order row, string scopeKey, ScryPolicyContext context) =>
        grants.Allows(scopeKey, row.Region);
}
```
<sup><a href='/samples/Sample.Server/RegionAccessPolicy.cs#L1-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-cachedRowPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So it runs in C# rather than in SQL, and the server remembers what it answered. What a query carries is a membership test over the keys this caller is allowed, which is why the LINQ on the page is what it would be for any other source — nothing about it says the policy is cached.

The `/permissions` page exists to make the remembering visible, since a cache that works looks exactly like one that is not there. It shows how many times the expensive decision has actually run, and three buttons move it:

| Action | Decisions | Why |
| --- | --- | --- |
| Run the query again | unchanged | Every row already has an answer. An ordinary policy would have re-filtered them all. |
| Revise an order | **+1** | Its `Revision` moves past the watermark this caller was decided up to, so that one row is decided again — and nothing tells the cache to do it. An inserted row is correct on its first read the same way. |
| Revoke a region | **+3** | The grant changed, which no column can see, so the server tells the cache; the caller's answers are thrown away and made again. |

The second and third are the two halves worth understanding. Nothing is called for the first:

<!-- snippet: cachedPolicyReadThrough -->
<a id='snippet-cachedPolicyReadThrough'></a>
```cs
// A row changed. Nobody tells the cache anything here: the next query sees a revision past the
// watermark this scope was decided up to, and decides that one row on the spot. An insert by
// any writer at all is correct on its first read for the same reason.
app.MapPost("/api/orders/{id:int}/touch", async (int id, SampleContext data) =>
{
    var order = await data.Orders.FindAsync(id);
    if (order is null)
    {
        return Results.NotFound();
    }

    // Named explicitly: Scry's async terminals and EF's are both in scope here, and they are
    // not the same method — this one has to run against the database.
    order.Revision = await EntityFrameworkQueryableExtensions.MaxAsync(data.Orders, _ => _.Revision) + 1;
    await data.SaveChangesAsync();
    return Results.NoContent();
});
```
<sup><a href='/samples/Sample.Server/Program.cs#L100-L118' title='Snippet source file'>snippet source</a> | <a href='#snippet-cachedPolicyReadThrough' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The third has to be, or the change never reaches a query at all:

<!-- snippet: invalidateCachedPolicy -->
<a id='snippet-invalidateCachedPolicy'></a>
```cs
// A grant moved. Nothing about any order changed, so no version column could notice and no
// query would ever decide those rows again — the cache has to be told, and telling it is part
// of the authorization path rather than a cache optimization.
app.MapPost("/api/grants/{region}", (string region, bool allowed, RegionGrants grants, ScryPolicyCache cache) =>
{
    grants.Set("sample", region, allowed);
    cache.InvalidateScope<Order>("sample");
    return Results.NoContent();
});
```
<sup><a href='/samples/Sample.Server/Program.cs#L88-L98' title='Snippet source file'>snippet source</a> | <a href='#snippet-invalidateCachedPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Order.Revision` is `[QueryIgnore]`d — a version column is server machinery, not query surface, and clients never see it. `Sample.Tests\CachedPolicyPageTests.cs` drives the page and asserts those three counts, so the table above is checked rather than claimed.


## What the traffic looks like

`Sample.Tests` snapshots the actual HTTP exchange for the first query:

<!-- snippet: WireFormatTests.EmployeeQueryWireFormat.verified.txt -->
<a id='snippet-WireFormatTests.EmployeeQueryWireFormat.verified.txt'></a>
```txt
[
  {
    RequestUri: {
      Path: http://localhost/api/query,
      Query: {
        q: {"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"member","path":"Active"}},{"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},{"$type":"select","projection":{"members":["Name","Status",{"name":"Manager","value":{"$type":"node","node":{"$type":"member","path":["Manager","Name"]}}},{"name":"Department","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}}],"stamp":"{scrubbed stamp}"}
      }
    },
    RequestMethod: GET,
    ResponseStatus: OK 200,
    ResponseHeaders: {
      Cache-Control: no-cache, private,
      Scry-Schema-Stamp: {Scrubbed},
      Scry-Url-Limit: 4096
    },
    ResponseContent: {"version":2,"kind":"List","payload":[{"name":"Aaron","status":"FullTime","manager":"Alice","department":"Engineering"},{"name":"Alice","status":"FullTime","manager":null,"department":"Engineering"},{"name":"Carol","status":"Contractor","manager":null,"department":"Sales"}],"stamp":"{scrubbed stamp}"}
  }
]
```
<sup><a href='/samples/Sample.Tests/WireFormatTests.EmployeeQueryWireFormat.verified.txt#L1-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-WireFormatTests.EmployeeQueryWireFormat.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Four projected columns requested, four returned. `Salary` is neither requested nor returnable.


## Integration tests

`/IntegrationTests` hosts the same model over a real ASP.NET Core test server against LocalDB, and exercises the full round trip through the typed client — including that a hand-crafted request naming an ignored property is rejected:

<!-- snippet: rawRequestRejected -->
<a id='snippet-rawRequestRejected'></a>
```cs
[Test]
public async Task DisallowedPropertyRejectedWith400()
{
    const string json = """
        {
          "version": 1,
          "root": "Employee",
          "pipeline": [
            {
              "$type": "where",
              "predicate": {
                "$type": "binary",
                "op": "GreaterThan",
                "left": { "$type": "member", "path": "Salary" },
                "right": { "$type": "const", "value": "100", "tag": "Decimal" }
              }
            }
          ]
        }
        """;

    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    using var response = await http.PostAsync("/api/query", content);

    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
}
```
<sup><a href='/IntegrationTests/HttpRoundTripTests.cs#L341-L368' title='Snippet source file'>snippet source</a> | <a href='#snippet-rawRequestRejected' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
