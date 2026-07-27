# Scry.Client

Client-side LINQ provider for [Scry](https://github.com/Papyrine/Scry). Write ordinary LINQ
against the source-generated query models; Scry captures it, serializes it to the query AST, and
sends it to the server. No EF dependency, so it stays small in a trimmed Blazor WebAssembly app.

This package also ships the Scry source generator, so a client project needs only a
`<ScryModelDll>` path to the server model's built DLL — never a reference to it.

<!-- snippet: clientModelPath -->
<a id='snippet-clientModelPath'></a>
```csproj
<!-- The server model, pointed at by path. NOT referenced. -->
<ScryModelDll>$(MSBuildThisFileDirectory)..\Sample.Model\bin\$(Configuration)\net10.0\Sample.Model.dll</ScryModelDll>
```
<sup><a href='/samples/Sample.Client/Sample.Client.csproj#L7-L10' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientModelPath' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: clientRegistration -->
<a id='snippet-clientRegistration'></a>
```cs
builder.Services.AddScoped(
    _ => new HttpClient
    {
        BaseAddress = new(builder.HostEnvironment.BaseAddress)
    });
builder.Services.AddScryClient("/api/query");
builder.Services.AddScoped<ScryQuery>();
```
<sup><a href='/samples/Sample.Client/Program.cs#L13-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: clientQuery -->
<a id='snippet-clientQuery'></a>
```cs
employees = await Query.Employee
    .Where(_ => _.Active)
    .OrderBy(_ => _.Name)
    .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
    .ToListAsync();
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L25-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientQuery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Docs: [Getting started](https://github.com/Papyrine/Scry/blob/main/docs/getting-started.md) ·
[Writing queries](https://github.com/Papyrine/Scry/blob/main/docs/querying.md) ·
[Source generator](https://github.com/Papyrine/Scry/blob/main/docs/source-generator.md)
