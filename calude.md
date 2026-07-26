# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Scry is

Type-safe, serializable LINQ from a client to a server-side EF Core model. The client writes ordinary
LINQ against **generated** query types, the LINQ is captured (never executed client-side) and
serialized to a restricted AST, and the server re-validates that AST against an allow-list, rebinds it
to the real EF types, executes it, and returns the projected rows. The client never references the
server model — it is pointed at the model DLL **by path**. See `readme.md` and `docs/` (start with
`docs/security.md`, `docs/source-generator.md`, `docs/wire-format.md`).

The governing assumption is that **the client is hostile**: generated code, LINQ, and the wire request
are all attacker-controlled. Every guarantee is enforced server-side at runtime. When touching the
wire format, validator, or annotations, treat `docs/security.md` as the spec.

## Solutions and build

There is **no root solution**. Three separate `.slnx` files, each with its own `Directory.Build.props`
and `Directory.Packages.props`:

- `src/Scry.slnx` — the shipped libraries + their unit tests. The main one.
- `samples/Scry.Samples.slnx` — Blazor WASM client, server, model, and Blazor/explorer UI tests.
- `IntegrationTests/IntegrationTests.slnx` — full HTTP round-trip tests (real Kestrel + SQL LocalDB).

```bash
dotnet build src/Scry.slnx
dotnet test src/Scry.slnx
dotnet test samples/Scry.Samples.slnx
dotnet test IntegrationTests/IntegrationTests.slnx
```

Run one test (NUnit + `dotnet test`):

```bash
dotnet test src/Scry.slnx --filter "FullyQualifiedName~SecurityTests.RejectsIgnoredProperty"
```

- .NET SDK is pinned via `global.json` (net10, prerelease allowed). Target framework is `net10.0`
  (the source generator additionally multi-targets `netstandard2.0`).
- `src/` builds with `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`, and NuGet audit turns any
  package vulnerability advisory into a build error. Keep the build warning-clean.
- Central Package Management: **never** put a version on a `PackageReference`; add/adjust the
  `PackageVersion` in the relevant `Directory.Packages.props`.

## Testing conventions

- **NUnit** for all tests, with **Verify** for snapshot assertions. `*.verified.*` files are the
  committed expectations; `*.received.*` are gitignored actuals. To accept an intended change, move
  the received file over the verified file (or use your Verify diff tool). Do not hand-edit verified
  files to make a test pass unless the change is genuinely intended.
- SQL-backed tests (`Scry.Tests`, `IntegrationTests`) use `EfLocalDb` — a real SQL Server LocalDB.
  They need LocalDB available on the machine.
- `NaughtyStrings` is used to fuzz string handling.

## Docs are generated — edit the source, not the output

`readme.md` and `docs/*.md` contain `<!-- snippet -->` regions produced by **MarkdownSnippets**
(wired into `Scry.Tests`). Building `Scry.Tests` regenerates them from the real source files. Never
hand-edit text inside a `snippet`/`endSnippet` region — change the referenced source and rebuild.

## Architecture / data flow

The pipeline crosses four projects. Key files to read at each stage:

**Client capture — `Scry.Client` (no EF dependency).**
- `ScryClient.Source<T>(name)` returns an `IQueryable<T>` backed by `ScryQueryProvider` /
  `ScryQueryable` — a capture-only provider that throws on synchronous enumeration.
- `ScryQueryableExtensions` holds the async terminals (`ToListAsync`, `FirstAsync`, `CountAsync`, …)
  and `ToScryRequest`, which run `QueryTranslator` to turn the captured expression tree into the wire
  AST and POST it via `ScryClient`.

**Wire AST — `Scry.Wire` (shared by client and server).**
- `QueryRequest` (root source name + pipeline), `QueryOp` (the closed operator set), `Expr` (the
  closed expression set), `Enums.cs` (`KnownFunction` — the only callable functions), `QueryResponse`,
  `ScryIntrospection`, `ScryJson` (the shared `JsonSerializerOptions`). Polymorphism uses a `$type`
  discriminator; unknown discriminators **fail** deserialization rather than being ignored. Adding a
  wire node means editing the `[JsonDerivedType]` list here.

**Server validate/execute — `Scry.Server` (public types live in namespace `Scry`, not `Scry.Server`).**
- `ScrySchema.Build(options)` builds the allow-list at startup from the **real** model assembly,
  independent of what the client was generated against.
- `ScryProcessor.Execute` is the single choke point (validation → allow-list → policies → shaping);
  use it for auditing/other transports. `QueryExecutor`, `QueryValidator`, `ExpressionBuilder`,
  `ProjectionPlan` do the work. `QueryValidator` runs to completion **before** anything is rebound;
  a rejected query never reaches EF Core.
- `ScryServiceExtensions`: `AddScry<TContext>` (DI + `AddPocoSource`) and `MapScry(pattern)` (the HTTP
  endpoint). `IReturnablePolicy<T>` / `ScryPolicyContext` implement per-source row policies applied
  **before** any client operator, so client filters can only narrow an authorized set.

**Annotations — `Scry.Annotations`.** Default-deny allow-list: `[Queryable]`, `[QueryableView]`,
`[QueryablePoco]` opt a type in; `[QueryIgnore]` hides a member; `[ReturnableWith]` attaches a policy.
A `[QueryablePoco]` has no table, so its rows must be supplied via `AddPocoSource` (server throws at
startup otherwise).

## The source generator (`Scry.SourceGenerator`)

Not published standalone — it is packed **inside** `Scry.Client` as an analyzer. It reads the server
model's built DLL from disk via `System.Reflection.Metadata` (`MetadataModelReader`, `ModelExtract`,
`ScryGenerator`); the assembly is never referenced, loaded, or executed, and only the allow-listed
surface is extracted. It emits, into the `Scry.Generated` namespace: one `{Type}QueryModel.g.cs` per
source (init-only props; navigations typed as the other query model), `ScryEnums.g.cs`, and
`ScryQuery.g.cs` (the `IQueryable<T>` entry point per source).

MSBuild wiring (see `src/Scry.Client/buildTransitive/Scry.Client.props` and
`docs/source-generator.md`): a consumer sets `<ScryModelDll>` to the model DLL path and adds a
`ProjectReference … ReferenceOutputAssembly="false"` to the model project **for build ordering only**.
Because Roslyn can't see an out-of-band file, `ComputeScryStamp` hashes the DLL into a second
compiler-visible property (`ScryModelStamp`) so the generator re-runs exactly when the model's
*contents* change. Referencing the projects directly (samples, integration tests) writes this wiring
out explicitly instead of importing the props file.

## Query explorer (`Scry.Explorer.Core`, `Scry.Explorer.Ui`, `Scry.Server.Explorer`)

Opt-in GraphiQL-style explorer that runs **Roslyn in the browser** (Blazor WASM) for real IntelliSense
against the allow-listed schema. Mapped via `MapScryExplorer` (Development-only by default; unmapped =
absent). `Scry.Explorer.Ui` is the WASM app; `Scry.Server.Explorer` packages its built assets
(`ExplorerAssets`).

⚠️ **Do not bump `Microsoft.CodeAnalysis.*` past the pinned 5.3.0, and do not move the Explorer UI to
net11**, without reading the long comment in `src/Directory.Packages.props`. Newer versions crash the
in-browser interpreter, and the blind spot is that `dotnet test` on `src/` stays green — only the
Explorer UI tests in `samples/Sample.Tests` catch it, and they present as timeouts.

## Code conventions

- Internal (non-public) types omit the file-scoped namespace and live in the global namespace; only
  public API types are namespaced. Each project's `GlobalUsings.cs` carries a `global using` for its
  own namespace so the global-namespace types can still reach the public ones.
- Lambda parameters are conventionally named `_` (e.g. `.Where(_ => _.Active)`).
- Line comments go on their **own line directly above** the code, never trailing. `.editorconfig`
  promotes many ReSharper hints to errors and the build enforces code style — match surrounding style.
