# F#

Scry's client is C#-shaped in exactly one place: the [source generator](source-generator.md), which is a Roslyn generator and so runs only in a C# project. Everything after it is language-neutral. The capture provider, the translator, the terminals, and the wire read expression trees, and an F# lambda handed to a LINQ extension method is converted into the same expression tree a C# lambda is. So an F# client needs one C# project between it and the model, and nothing else changes.

The sample carries a working one: `Sample.QueryModels` hosts the generator's output, `Sample.FSharp` writes queries against it, and `Sample.FSharp.Tests` runs them through the real server.


## The query models project

A C# class library with no source of its own. It points at the model DLL by path and references `Scry.Client`, which brings the generator with it:

<!-- snippet: queryModelsProject -->
<a id='snippet-queryModelsProject'></a>
```csproj
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <IsPackable>false</IsPackable>
  <!-- The server model, pointed at by path. NOT referenced. -->
  <ScryModelDll>$(MSBuildThisFileDirectory)..\Sample.Model\bin\$(Configuration)\net10.0\Sample.Model.dll</ScryModelDll>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="..\..\src\Scry.Client\Scry.Client.csproj" />
</ItemGroup>
```
<sup><a href='/samples/Sample.QueryModels/Sample.QueryModels.csproj#L7-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryModelsProject' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Everything the generator emits — the query model per source, the re-emitted enums, and the `ScryQuery` entry point — is this project's whole public surface. The generator wiring is the same as any C# client's: automatic with the NuGet package, written out in the sample because it uses project references ([Source generator](source-generator.md#wiring)).

The generated types always land in the `Scry.Generated` namespace, so one consumer can see one such project. The sample's Blazor client generates its own copy, which is why the F# tests host the server themselves rather than referencing `Sample.Server`: it carries the Blazor client, and two copies of `Scry.Generated.ScryQuery` among one assembly's references is an ambiguity the compiler refuses.


## The F# project

References the query models project, and through it `Scry.Client`. The model is not referenced:

<!-- snippet: fsharpProjectReference -->
<a id='snippet-fsharpProjectReference'></a>
```fsproj
<ItemGroup>
  <!-- The generated query models, and through them Scry.Client. The model itself is not referenced. -->
  <ProjectReference Include="..\Sample.QueryModels\Sample.QueryModels.csproj" />
</ItemGroup>
```
<sup><a href='/samples/Sample.FSharp/Sample.FSharp.fsproj#L17-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpProjectReference' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

One F#-specific note: under central package management the F# SDK turns its implicit `FSharp.Core` reference off, so a project in such a tree references it by hand. Without it the assembly builds — the compiler falls back to the SDK's own copy — and then fails to load every type at runtime.


## Writing queries

Queries are the LINQ extension methods with F# lambdas. Where the body is a member chain, the `_.Member` shorthand is the whole lambda; a body that constructs a record or applies an operator names its parameter. A projection is a record, named or anonymous, whose fields name the members the response comes back keyed by:

<!-- snippet: fsharpProjectionType -->
<a id='snippet-fsharpProjectionType'></a>
```fs
/// A shape the client declares for itself, as the Blazor sample's EmployeeRow is. The constructor's
/// parameters name the projection's members, so the response comes back keyed by these names.
type EmployeeRow =
    { Name: string
      Status: Status
      Manager: string
      Department: string }
```
<sup><a href='/samples/Sample.FSharp/Queries.fs#L8-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpProjectionType' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: fsharpQuery -->
<a id='snippet-fsharpQuery'></a>
```fs
/// A filter, an ordering, and a projection that reaches through two navigations. Each lambda is
/// converted to an expression tree at the call site, as a C# lambda is, so the request that
/// leaves is the one the C# spelling sends.
let activeEmployees (query: ScryQuery) =
    query.Employee
        .Where(_.Active)
        .OrderBy(_.Name)
        .Select(fun e ->
            { Name = e.Name
              Status = e.Status
              Manager = e.Manager.Name
              Department = e.Department.Name })
```
<sup><a href='/samples/Sample.FSharp/Queries.fs#L20-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpQuery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An F# record compiles to a constructor taking every field, and the translator reads member names from constructor parameters, capitalized — the rule a C# record follows ([Projections](querying.md#projections)).

An anonymous record declares nothing:

<!-- snippet: fsharpAnonymousRecord -->
<a id='snippet-fsharpAnonymousRecord'></a>
```fs
/// An anonymous record declares nothing. Its fields may be written in any order — the compiler
/// sorts them by name, and the query is the same either way.
let headcount (query: ScryQuery) =
    query.Employee
        .GroupBy(_.Department.Name)
        .Select(fun g -> {| Headcount = g.Count(); Department = g.Key |})
```
<sup><a href='/samples/Sample.FSharp/Queries.fs#L35-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpAnonymousRecord' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The compiler sorts an anonymous record's fields by name. Written in another order, it binds each field to a variable first — a `let` per field, in the order written — and constructs the record from the variables, so that the fields still evaluate in the order written. The translator inlines those bindings: a query reads the row and computes nothing else, so reading the bound expression where the variable was read is the same query. The same inlining covers a `let` written by hand:

<!-- snippet: fsharpLet -->
<a id='snippet-fsharpLet'></a>
```fs
/// A let inside the lambda is inlined: wherever the binding is read, the query reads what was
/// bound to it, so the row is read twice here and nothing is computed on the client.
let namedLike (query: ScryQuery) (fragment: string) =
    query.Employee
        .Where(fun e ->
            let name = e.Name.ToLower()
            name.Contains fragment && name.Length > 2)
        .OrderBy(_.Name)
        .Select(fun e -> {| Id = e.Id; Name = e.Name |})
```
<sup><a href='/samples/Sample.FSharp/Queries.fs#L56-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpLet' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Values captured from the enclosing scope are evaluated on the client and sent as constants, as C# closures are. The generated models spell an optional column as `Nullable<int>` rather than `int option`, so it compares through the nullable operators:

<!-- snippet: fsharpClosure -->
<a id='snippet-fsharpClosure'></a>
```fs
/// Parameterized by values captured from the enclosing scope, which are evaluated here and sent
/// as constants. A nullable member is compared with the nullable operators, which lift the
/// constant the way C# lifts it.
let reportsTo (query: ScryQuery) (managerId: int) (top: int) =
    query.Employee
        .Where(fun e -> e.ManagerId ?= managerId)
        .OrderBy(_.Name)
        .Take(top)
        .Select(fun e -> {| Name = e.Name; Status = e.Status |})
```
<sup><a href='/samples/Sample.FSharp/Queries.fs#L44-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpClosure' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The terminals are the client's own, so a query runs inside a `task` like any other awaitable, and a terminal taking a predicate of its own translates it the same way:

<!-- snippet: fsharpTerminals -->
<a id='snippet-fsharpTerminals'></a>
```fs
/// The terminals are the client's own, so a query runs inside a task like any other awaitable.
let activeEmployeesAsync (query: ScryQuery) =
    task {
        let! rows = (activeEmployees query).ToListAsync()
        return rows
    }

/// A terminal that takes a predicate of its own translates it the same way.
let activeCountAsync (query: ScryQuery) =
    query.Employee.CountAsync(_.Active)
```
<sup><a href='/samples/Sample.FSharp/Queries.fs#L68-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpTerminals' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## What to avoid

**`query { }`.** F#'s query builder rewrites a `select` of a record or a tuple into two `Select`s — one onto an intermediate object of its own and one back — and the server accepts one `Select` per query, so the request is rejected. A `query { }` that only filters and orders translates; a projection needs the extension methods.

**Nullness checking.** The sample leaves F#'s nullness checking off. With it on, reading a navigation inside a query lambda warns that the navigation may be null — which is true of the model and not a problem for the query: a null navigation is null in SQL too, and the translated query reads through it as the C# spelling's `_.Manager!.Name` does.

**Bare values.** A projection must construct an object; `Select(fun e -> e.Name)` is refused, as it is from C# ([Projections](querying.md#projections)).


## Testing from F#

`Sample.FSharp.Tests` hosts the sample server in-process, against LocalDB, and registers it from F#:

<!-- snippet: fsharpServer -->
<a id='snippet-fsharpServer'></a>
```fs
/// The sample server hosted in-process, as Sample.Tests hosts it, against a LocalDB database cloned
/// from a seeded template. Registered from F# to show that the server side reads the same either way.
type ScryServer private (app: WebApplication, database: SqlDatabase<SampleContext>) =
    // A LocalDB instance of its own. The instance is named after the context by default, which
    // Sample.Tests already uses, and the two projects run in parallel under one dotnet test: both
    // rebuilding the one template at once deadlocks inside SQL Server.
    static let sqlInstance =
        new SqlInstance<SampleContext>(
            constructInstance = (fun builder -> new SampleContext(builder.Options)),
            buildTemplate =
                (fun context ->
                    SampleContext.Initialize context
                    Task.CompletedTask),
            storage = Storage.FromSuffix<SampleContext> "FSharp")

    static member StartAsync() =
        task {
            let! database = sqlInstance.Build()
            let builder = WebApplication.CreateBuilder()
            builder.WebHost.UseTestServer() |> ignore

            builder.Services.AddDbContext<SampleContext>(fun (options: DbContextOptionsBuilder) ->
                options.UseSqlServer database.ConnectionString |> ignore)
            |> ignore

            builder.Services.AddScry<SampleContext>(fun options ->
                options.AddPocoSource(fun _ -> Holiday.Seed())
                options.AddAttachmentPolicy<Department, HandbookPolicy>()
                options.AddAttachmentPolicy<Employee, PhotoPolicy>())
            |> ignore

            let app = builder.Build()
            app.MapScry "/api/query" |> ignore
            do! app.StartAsync()
            return new ScryServer(app, database)
        }

    /// The generated entry point over an HTTP client into the hosted server.
    member _.Query = ScryQuery(ScryClient.ForHttp(app.GetTestClient(), "/api/query"))

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    do! app.DisposeAsync()
                    do! database.DisposeAsync()
                }
                :> Task)
```
<sup><a href='/samples/Sample.FSharp.Tests/ScryServer.fs#L24-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-fsharpServer' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Each query is snapshotted twice: the request as it would travel, and the rows the server returns for it.
