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
}
```
<sup><a href='/samples/Sample.Model/Entities/Employee.cs#L3-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryableEntity' title='Start of snippet'>anchor</a></sup>
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
}
```
<sup><a href='/samples/Sample.Model/Entities/Order.cs#L3-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryableOrder' title='Start of snippet'>anchor</a></sup>
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
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L26-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-serverRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Holiday` has no table, so its data is registered explicitly — see [POCO sources](server.md#poco-sources). `MaxPageSize` is lowered from the default 1000 to 200.

<!-- snippet: mapScry -->
<a id='snippet-mapScry'></a>
```cs
app.MapScry("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L55-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScry' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='/samples/Sample.Server/Program.cs#L58-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapExplorer' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The sample always exposes the explorer so it can be browsed without setting an environment. A real app should leave the default Development-only guard in place, or replace it with an authorization check — see [Query explorer](explorer.md).


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


## What the traffic looks like

`Sample.Tests` snapshots the actual HTTP exchange for the first query:

<!-- snippet: WireFormatTests.EmployeeQueryWireFormat.verified.txt -->
<a id='snippet-WireFormatTests.EmployeeQueryWireFormat.verified.txt'></a>
```txt
[
  {
    RequestUri: http://localhost/api/query,
    RequestMethod: POST,
    RequestContent: {"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"member","path":["Active"]}},{"$type":"orderBy","key":{"$type":"member","path":["Name"]},"descending":false},{"$type":"select","projection":{"members":[{"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Name"]}}},{"name":"Status","value":{"$type":"node","node":{"$type":"member","path":["Status"]}}},{"name":"Manager","value":{"$type":"node","node":{"$type":"member","path":["Manager","Name"]}}},{"name":"Department","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}}],"stamp":"8yskMW95UPUIz0wo"},
    ResponseStatus: OK 200,
    ResponseHeaders: {
      Scry-Schema-Stamp: 8yskMW95UPUIz0wo
    },
    ResponseContent: {"version":2,"kind":"List","payload":[{"name":"Aaron","status":"FullTime","manager":"Alice","department":"Engineering"},{"name":"Alice","status":"FullTime","manager":null,"department":"Engineering"},{"name":"Carol","status":"Contractor","manager":null,"department":"Sales"}],"stamp":"8yskMW95UPUIz0wo"}
  }
]
```
<sup><a href='/samples/Sample.Tests/WireFormatTests.EmployeeQueryWireFormat.verified.txt#L1-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-WireFormatTests.EmployeeQueryWireFormat.verified.txt' title='Start of snippet'>anchor</a></sup>
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
                "left": { "$type": "member", "path": ["Salary"] },
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
<sup><a href='/IntegrationTests/HttpRoundTripTests.cs#L279-L306' title='Snippet source file'>snippet source</a> | <a href='#snippet-rawRequestRejected' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
