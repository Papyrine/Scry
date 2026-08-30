# Scry.Client

Client-side LINQ provider for [Scry](https://github.com/Papyrine/Scry). Write ordinary LINQ against the source-generated query models; Scry captures it, serializes it to the query AST, and sends it to the server. No EF dependency, so it stays small in a trimmed Blazor WebAssembly app.

This package also ships the Scry source generator, so a client project needs only a `<ScryModelDll>` path to the server model's built DLL — never a reference to it.

<!-- snippet: clientModelPath -->
<a id='snippet-clientModelPath'></a>
```csproj
<!-- The server model, pointed at by path. NOT referenced. -->
<ScryModelDll>$(MSBuildThisFileDirectory)..\Sample.Model\bin\$(Configuration)\net10.0\Sample.Model.dll</ScryModelDll>
```
<sup><a href='/samples/Sample.Client/Sample.Client.csproj#L7-L10' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientModelPath' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Register the client over a **named** `HttpClient`, so its base address — and any handler pipeline it grows — stays separate from every other call the application makes:

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

Blazor WebAssembly is the exception: there is one `HttpClient`, the browser backs it, and it already points at the app's own origin, so nothing needs disambiguating and the shorter overload avoids pulling `Microsoft.Extensions.Http` into the payload.

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

Docs: [Getting started](https://github.com/Papyrine/Scry/blob/main/docs/getting-started.md) · [Writing queries](https://github.com/Papyrine/Scry/blob/main/docs/querying.md) · [Source generator](https://github.com/Papyrine/Scry/blob/main/docs/source-generator.md)
