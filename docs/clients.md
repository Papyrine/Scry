# Client hosts

Scry's client half is an `HttpClient` and a generated entry point. Nothing in it is bound to a browser, a UI framework, or a
hosting model, so the same LINQ runs from a Blazor WebAssembly page, a WPF or Windows Forms window, a console tool, or a
background service.

What a client project needs is the same everywhere, and is covered in [Getting started](getting-started.md) and
[Source generator](source-generator.md):

- a `<ScryModelDll>` property pointing at the server model's built DLL
- a `ProjectReference` to the model project with `ReferenceOutputAssembly="false"`, for build ordering alone
- a reference to `Scry.Client`, which brings the generator and the MSBuild targets that feed it the path

The target framework of the client is its own business. A `net10.0-windows` WPF project reads the same `net10.0` model DLL a
Blazor project does — the framework in that path is the **model's**, not the client's.

The rest of this page is what differs between hosts.


## Getting a ScryClient


### Without a container

`ScryClient.ForHttp` takes an `HttpClient` and the endpoint [`MapScry`](server.md) was given. A console tool needs nothing
past those two:

<!-- snippet: consoleClientSetup -->
<a id='snippet-consoleClientSetup'></a>
```cs
using var http = new HttpClient
{
    BaseAddress = new(serverAddress)
};
var query = new ScryQuery(ScryClient.ForHttp(http, "/api/query"));
```
<sup><a href='/samples/Sample.ConsoleClient/Program.cs#L10-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-consoleClientSetup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The generated `ScryQuery` takes a `ScryClient` in its constructor, so no container is involved at any point. This is also how
the F# tests build a client against an in-process server.


### Through IHttpClientFactory

An app that already has a container should name the client Scry sends on. That keeps Scry's base address, and any handler
pipeline it grows — an auth `DelegatingHandler`, a retry policy, the [304 cache handler](caching.md) — separate from every
other call the app makes:

<!-- snippet: desktopClientRegistration -->
<a id='snippet-desktopClientRegistration'></a>
```cs
services.AddHttpClient(
    "scry",
    _ => _.BaseAddress = new(ServerAddress));
services.AddScryClient(
    "/api/query",
    _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
services.AddScoped<ScryQuery>();
```
<sup><a href='/samples/Sample.WpfClient/App.xaml.cs#L21-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-desktopClientRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The factory is reached through a delegate rather than being a dependency of `Scry.Client`, so an app that does not otherwise
want `Microsoft.Extensions.Http` does not acquire it by referencing Scry.


### The ambient overload

`AddScryClient(endpoint)` takes whichever `HttpClient` the container holds. That suits Blazor WebAssembly, where there is
exactly one, the browser backs it, and it already points at the app's own origin. Anywhere else a bare `HttpClient`
registration is discouraged to begin with, and an ambient one may belong to another API — see
[Getting started](getting-started.md).


### Without HTTP

The public `ScryClient` constructor takes transport delegates, so a client can run against an in-process processor with no
HTTP at all. See [hosting without HTTP](server.md).


## Lifetime

`AddScryClient` registers the client **scoped**. It records the schema stamp each response advertises and raises
[`SchemaStaleDetected`](schema-versioning.md) at most once, so a fresh instance per injection would reset that and never
report drift.

A web request supplies that scope on its own. A desktop app has none, so it opens one at startup and holds it for as long as
the app runs:

<!-- snippet: desktopClientScope -->
<a id='snippet-desktopClientScope'></a>
```cs
scope = provider.CreateScope();
var query = scope.ServiceProvider.GetRequiredService<ScryQuery>();
```
<sup><a href='/samples/Sample.WpfClient/App.xaml.cs#L37-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-desktopClientScope' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Resolving from the root provider instead would throw once scope validation is on, and a scope per window would lose drift
detection. A client built by hand with `ScryClient.ForHttp` has the same requirement met by holding the one instance.


## What differs by host

| | Blazor WebAssembly | WPF / Windows Forms | Console / service |
| --- | --- | --- | --- |
| `HttpClient` | ambient, browser-backed | named, through `IHttpClientFactory` | either, or constructed by hand |
| Scope | per request | one for the life of the app | one for the life of the process |
| HTTP cache | the browser's | none; the [304 handler](caching.md) supplies it | none; same handler |
| [Debug sidecar](sidecar.md) | available | not available | not available |
| Trimming | the reason the client carries no EF dependency | optional | optional |

Everything else — the generated models, the LINQ surface, [paging](paging.md), [batching](batching.md),
[attachments](attachments.md), [row policies](policies.md), and every server-side guarantee — is identical, because the
server sees the same wire request whichever host sent it.


## The sidecar is the exception

`Scry.Client` ships one piece that is host-bound: the [debug sidecar](sidecar.md) is a Razor component, so it renders in a
Blazor app and nowhere else. Referencing `Scry.Client` from a desktop or console project carries a few Blazor assemblies that
go unused as a result; nothing needs disabling for that.


## Clients that are not C\#

The generator is a Roslyn generator, so only a C# project runs it. A client in another .NET language references a C# project
holding the output and nothing else. See [F#](fsharp.md).


## The samples

`/samples` carries one server and five clients against it, each writing the same query:

| Project | Shape |
| --- | --- |
| `Sample.WebClient` | Blazor WebAssembly, ambient `HttpClient`, sidecar and 304 handler wired |
| `Sample.ConsoleClient` | no container, `ScryClient.ForHttp`, rows written to stdout |
| `Sample.WpfClient` | `IHttpClientFactory`, one app-lifetime scope, bound to a `DataGrid` |
| `Sample.WinFormsClient` | the same registration, bound to a `DataGridView` |
| `Sample.FSharp` | queries over `Sample.QueryModels`, run by `Sample.FSharp.Tests` |

See [Sample](sample.md) for running them.
