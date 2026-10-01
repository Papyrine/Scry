# The model as a package

A model is often shipped as a NuGet package: one team owns the entities and their `DbContext`, and the web app that serves them, and the clients that query it, each take a version of the package. Scry needs nothing extra for that. The server references the package as it would any other. The client points the [source generator](source-generator.md) at the DLL inside the package, by path, exactly as it points at a model project's build output. What changes is how that path is found, and what keeps the package itself out of the client.

The sample carries a working one: `Sample.ModelPackageClient` is generated from `Sample.Model` packed into a local folder, and builds only while neither the model nor EF Core reaches it ([Sample](sample.md)).


## The client

<!-- snippet: modelPackageReference -->
<a id='snippet-modelPackageReference'></a>
```csproj
<ItemGroup>
  <!-- Downloaded for the generator to read. Nothing from it is compiled against or shipped. -->
  <PackageReference Include="Sample.Model"
                    ExcludeAssets="all"
                    PrivateAssets="all"
                    GeneratePathProperty="true" />
</ItemGroup>
<PropertyGroup>
  <!-- The model's own target framework, not this project's. -->
  <ScryModelDll>$(PkgSample_Model)\lib\net10.0\Sample.Model.dll</ScryModelDll>
</PropertyGroup>
```
<sup><a href='/samples/Sample.ModelPackageClient/Sample.ModelPackageClient.csproj#L19-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-modelPackageReference' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Each part of the reference does one job:

- **`ExcludeAssets="all"`** — the package is downloaded for the generator to read, and nothing from it is compiled against or shipped. NuGet applies that to everything the package brings with it, so EF Core and its database provider stay out too. It is the boundary a model project's `ReferenceOutputAssembly="false"` keeps ([The path-not-reference design](source-generator.md#the-path-not-reference-design)). A plain `PackageReference` would make the model a real reference of the client: compiled against, shipped, and for a Blazor WebAssembly client downloaded by every browser along with EF Core.
- **`PrivateAssets="all"`** — nothing referencing the client inherits the package: not the server that hosts a WebAssembly client, nor anything consuming a client that is itself packed.
- **`GeneratePathProperty="true"`** — restore defines `$(Pkg…)`, the package's folder in the NuGet cache, named for the package with each `.` as `_`. It is written to `obj/*.nuget.g.props`, which loads before the project body, so `ScryModelDll` can be built from it.
- **`lib\net10.0`** is the framework the *model* targets — the build the server runs — not the client's.

No ordering reference is needed: restore puts the DLL in place before anything builds. The generator reads the model again whenever the DLL's content changes ([Why the stamp](source-generator.md#why-the-stamp)), and a package's content changes only with its version.

With the `Scry.Client` package that is the whole client setup. The sample writes the generator wiring out beside it only because it references `Scry.Client` as a project ([Project references instead of the package](source-generator.md#project-references-instead-of-the-package)).


## One version on both sides

The server and every client have to read the same model, or each client reports itself stale. Under [central package management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management) the model's version is written once, in `Directory.Packages.props`, so a server and the clients built beside it move together by construction. A client built anywhere else — a desktop app, another repository — takes the same bump as a change of its own. Until it ships, a deployed client generated against the earlier version keeps working through additive changes, and learns it is behind from the schema stamp ([Detecting a stale client](schema-versioning.md#detecting-a-stale-client)).

Scry's own packages version together too. The model package is compiled against `Scry.Annotations`, the server reads it with `Scry.Server`, and the client with the generator inside `Scry.Client`. All three read the same attributes, so they belong on one Scry version.


## The server

The server references the model package as an ordinary dependency. Scry reads the annotated types from the model assembly, which is the `DbContext`'s own assembly unless `ScryOptions.ModelAssembly` names another. A package that ships the entities while the host declares the context, or derives one of its own from a context the package declares, names the package's assembly:

```cs
builder.Services.AddScry<StoreContext>(_ =>
    _.ModelAssembly = typeof(Customer).Assembly);
```

An assembly with nothing opted in is refused at startup, naming the assembly it read, rather than served as a model with no sources ([Server](server.md#registration)).


## What the package has to hold

A client is generated from one assembly, the DLL `ScryModelDll` names, and the server refuses at startup whatever a client generated from that assembly alone could never see ([Not exposed](annotations.md#not-exposed)). So the package's own assembly holds:

- **Every opted-in type, and the bases its members come from.** A base the model declares is read whether or not it opted in, generic ones included: `Order : Entity<int>` exposes `Entity`'s `Id` as an `int`. A base in another package is not read, so a member inherited from one is refused until it is hidden or declared in the model ([Inheritance](annotations.md#inheritance)).
- **Every enum an exposed member reads, and every class a command answers with.**
- **Every marking an override needs.** A member overriding one declared in another assembly repeats the `[QueryIgnore]`, `[Sensitive]`, `[Attachment]`, `[QueryableCollection]`, `[Key]` or `[CommandIgnore]` that declaration carries.

`Scry.Annotations` targets `net10.0`, so a package referencing it does too.

**Keep `Scry.Server` out of the package.** A policy named by `[ReturnableWith]`, `[AttachmentWith]` or a command's `Policy` lives in the model, so the model depends on `Scry.Server`, and through it on the ASP.NET Core shared framework. `ExcludeAssets` keeps the server's assemblies out of a client, but not that framework reference, which NuGet carries regardless: a Blazor WebAssembly client then fails to build, there being no ASP.NET Core runtime for the browser. Registered in code — `options.AddPolicy<TEntity, TPolicy>()`, with `AddAttachmentPolicy` and `AddCommandPolicy` beside it — a policy lives in the server, and the package depends on nothing but `Scry.Annotations` and EF Core.


## Troubleshooting

**`Scry: the model assembly '\lib\net10.0\…' was not found`, the path starting at the root of a drive.** `$(Pkg…)` was empty: the reference lacks `GeneratePathProperty="true"`, or the property is spelled differently from the one restore generated. `obj/*.nuget.g.props` lists the names it wrote.

**The client compiles against the model's types, or ships EF Core.** The reference lacks `ExcludeAssets="all"`.

**`NETSDK1082: There was no runtime pack for Microsoft.AspNetCore.App available for the specified RuntimeIdentifier 'browser-wasm'`.** The model package depends on `Scry.Server`, whose framework reference reaches a WebAssembly client through any reference to the package. Move the policies the model names into the server ([What the package has to hold](#what-the-package-has-to-hold)).

**The server starts with nothing to query.** It does not: it refuses, naming the assembly it read. Set `ScryOptions.ModelAssembly` to the package's assembly.
