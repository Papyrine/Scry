# Source generator

The generator lives in `Scry.SourceGenerator`. It is not a standalone package — it is packed inside
`Scry.Client` as an analyzer, so a client project that references `Scry.Client` already has it.

## The path-not-reference design

The generator reads the server model's **built DLL from disk** using `System.Reflection.Metadata`.
The assembly is never referenced by the client project, never loaded into the compiler, and never
executed. Only the allow-listed surface is extracted from its metadata tables.

That is what lets a Blazor WebAssembly client be strongly typed against a server-side EF Core model
without dragging EF Core, connection strings, or the non-allow-listed members of the model into the
client's dependency graph or its shipped output.

## Wiring

Two things are needed in the client project. First, the path the generator reads:

snippet: clientModelPath

Second, a project reference that exists purely for **build ordering**:

```xml
<ProjectReference Include="..\Sample.Model\Sample.Model.csproj" ReferenceOutputAssembly="false" />
```

`ReferenceOutputAssembly="false"` means no assembly reference is added — only the ordering
constraint. Without it the generator races the model build and reads a stale or missing DLL.

Everything else is supplied by the `buildTransitive/Scry.Client.props` file that ships in the
`Scry.Client` package:

snippet: buildTransitiveProps

### Why the stamp

Roslyn's incremental pipeline can only see inputs that are declared to it. The DLL is read out of
band, so from Roslyn's point of view the input is just a *path string* — which does not change when
the file's contents do. A build that changes the model but not its location would leave the
generator's cached output in place.

`ComputeScryStamp` hashes the DLL and surfaces the hash as a second compiler-visible property. The
generator combines path and stamp into one pipeline input, so the model is re-read exactly when its
contents change, and not otherwise.

The extracted model is then compared structurally (via `EquatableArray<T>`), so a change to the model
assembly that leaves the *queryable surface* untouched — an unrelated method, a private field — does
not trigger regeneration downstream.

### Project references instead of the package

When referencing the projects directly (as the sample and integration tests do), the props file is
not imported, so the wiring is written out explicitly:

snippet: clientGeneratorWiring

## What is emitted

Everything lands in the `Scry.Generated` namespace.

### One query model per source

`{TypeName}QueryModel.g.cs`:

snippet: GeneratorTests.EntitiesViewPocoAndEnum#EmployeeQueryModel.g.verified.cs

Note what is *absent*: `Salary` is `[QueryIgnore]`, and `Department`/`DepartmentId` appear only if
`Department` is itself opted in.

Properties are `init`-only. A reference navigation is emitted as a nullable reference to the *other
query model*, so `e.Manager!.Name` type-checks and traverses. A non-nullable `string` gets
` = null!;` to satisfy nullable analysis.

### Re-emitted enums

`ScryEnums.g.cs` contains every enum reachable from an exposed member, with its members in
declaration order:

snippet: GeneratorTests.EntitiesViewPocoAndEnum#ScryEnums.g.verified.cs

This is why the client can write `e.Status == Status.FullTime` without referencing the model.

### The entry point

`ScryQuery.g.cs` exposes one `IQueryable<T>` per allow-listed source:

snippet: GeneratorTests.EntitiesViewPocoAndEnum#ScryQuery.g.verified.cs

Register it alongside the client:

snippet: clientRegistration

## Type mapping

| Model member type | Generated |
| --- | --- |
| `bool`, `char`, `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double` | the C# keyword |
| `string` | `string` (with ` = null!;`) |
| `decimal` | `decimal` |
| `DateTime`, `DateOnly`, `TimeOnly`, `DateTimeOffset`, `TimeSpan`, `Guid` | `global::System.X` |
| an `enum` | the enum name, re-emitted into `ScryEnums.g.cs` |
| another opted-in type | `{Type}QueryModel?` |
| a nullable value type | the above with `?` |
| anything else | omitted |

## Diagnostics

| ID | Severity | Meaning |
| --- | --- | --- |
| `SCRY001` | Error | Failed to read the Scry model assembly. The message carries the underlying reason. |
| `SCRY002` | Error | Two queryable types resolve to the same source name. |

`SCRY001` is reported when the DLL exists but cannot be parsed — corrupt, truncated, or not a managed
assembly.

`SCRY002` guards the source-name clash that would otherwise emit duplicate properties on `ScryQuery`
and surface as a `CS0102` on generated code the user cannot see. Give one of the types a distinct
[`Name`](annotations.md#naming-a-source). The server rejects the same clash at startup.

## Troubleshooting

**Nothing is generated; `ScryQuery` does not exist.**
The path in `ScryModelDll` is empty or does not resolve to an existing file, so the generator
produces nothing at all rather than failing the build. Check the path against `$(Configuration)` and
the model's target framework — a `Release` client pointing at a `Debug` model path is the usual
cause. When consuming the NuGet package the `EnsureScryModel` target catches this and fails the build
with:

```
Scry: the model assembly '...' was not found. Ensure the model project is referenced with
ReferenceOutputAssembly="false" so it builds first.
```

**A source is missing from `ScryQuery`.**
The type is not opted in. Add `[Queryable]`, `[QueryableView]`, or `[QueryablePoco]` — see
[Annotations](annotations.md).

**A property is missing from a query model.**
It is `[QueryIgnore]`d, has no public instance getter, or its type is neither a scalar nor another
opted-in type. Collection navigations are always omitted.

**Generated code is stale after changing the model.**
This is what the stamp exists to prevent, so first confirm the model project actually rebuilt. In
Rider or Visual Studio the analyzer host caches generator assemblies; a restart clears it. The
generator's `AssemblyVersion` tracks the package version specifically so an upgraded package gets a
distinct identity rather than serving a cached, frozen generator.

**Build order flakiness in CI.**
Confirm the `ReferenceOutputAssembly="false"` project reference is present. Without it, nothing
orders the model build ahead of the client compile.

## Reading generated code

Add this to the client project to write the generated files to disk:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```
