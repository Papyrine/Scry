# Commands: write support for Scry

## Revision 2026-09-21: after live queries (`3cc464e`)

Live queries are on `main`: a query answered again whenever its answer changes, over SSE (`POST {p}/subscribe`) or a SignalR hub. It is re-run on change signals (`ScryChangeInterceptor`, `ScryChanges.Notify`, policy invalidations, a change probe, a poll floor), carried between nodes by `IScryChangeBackplane` (Redis, MessagePipe, NServiceBus), and consumed as a stream, a callback (`Subscribe`) or an `IObservable`. About half of the first draft's push machinery existed to keep screens current after writes. Live queries now do that job. What changed:

1. **Screens stay current through live queries.** A page's query is `.Live()`. A handler's save reaches it through the interceptor, a worker's through the NServiceBus backplane, and anything else through the probe or the poll. The first draft's refetch-on-outcome, `no-cache` re-reads and `ActivityChanged` refetches are gone.
2. **No second push channel.** The shared `/events` stream, the `Scry-Client` id, the replay ring, the receipt poll and the client's `ScryChannel` are gone. A command decided within the sync window answers JSON. One that is not streams its receipt on its own response, in the live-query framing, and a client re-attaches by id after a cut. Multiplexing many held streams is what HTTP/2 or the hub already do for live queries. *(This changes the "outcome over a push channel, disconnected from the HTTP response" decision: the outcome now arrives on the command's own response, streamed.)*
3. **Other users' in-flight activity is dropped from this round.** That removes `/watch`, `.Watched()`, `activity` events and `ScryActivity`. Their completed work arrives as the rows changing. A design for the in-flight part on the live-query transport is under [Out of scope](#out-of-scope). *(This drops a stated requirement.)*
4. **Commands are off until turned on.** `MaxPendingCommands` defaults to zero and maps no route, as `MaxSubscriptions` does. A host with commands off needs no handlers, and a host that turns them on routes all of them. That matters now that there are seven hosts over `Sample.Model`: the web server, three backplane servers, and the test servers.
5. **Every transport gets commands.** `ScryProcessor.SendCommand` is the seam, with the limits enforced there, as for `Subscribe`. The hub gains `Command`/`Receipt`/`Capabilities`, and `ScryClient` gains delegates beside `subscribeTransport`. The first draft's "custom transports refuse commands" is gone. There are three new `ScryErrorCode`s, because the hub has no status line.
6. **NServiceBus joins the existing `Scry.Server.NServiceBus`.** Its worker behavior already publishes what a message's handlers saved, and the command reply leaves beside that publish. The existing `RepriceOrder` sample message becomes the annotated bus command, which replaces the first draft's Rebus-routed sample step.
7. **A completed targeted command notifies its target** (`ScryChanges.Notify`). That covers `ExecuteDelete`/`ExecuteUpdate` handlers and workers that report nothing.
8. **`Can*` members are live inside a live query**, re-decided on every run.
9. Smaller changes:
   - The bus completion contract moves out of `Scry.Annotations` into each bus package, as `ScryChanged` did.
   - `SubscriptionCaller` becomes `Caller`.
   - Client settings are `ScryClient` properties, like `Reconnect`.
   - The sidecar gains a `Command` kind, not `Channel`.
   - Live queries settle three of the first draft's flags.
   - Line references are re-derived after the merge, and the inline generator wiring now has six copies, since the desktop samples arrived.

Unchanged: authoring, payload rules, key binding, the generator and its diagnostics, the facade, the stamp and introspection versions, capability rebinding, binding, policies, the one-answer 404, retention, audit, and the explorer. The four bus adapters stay in this round.

**Samples pass (same day).** A second pass over every sample project found three places commit 8 would not have worked as written:
- The F# test server cannot link the C# handlers.
- A host with commands on must route every command.
- One delete would leave nothing in the sample deletable, and nothing could change `Active`.

It also found hosts and clients the plan left out. Commit 8 now adds:
- a shared `Sample.CommandHandlers` project;
- a `SetEmployeeActive` command, and a client-visible failure (deleting a manager);
- a type-level denial that a test can switch on;
- a distinct command angle per client (console, WinForms, WPF, F#);
- commands on the backplane hosts, in place of their reprice endpoint;
- the live pages' Reprice over the selected transport, so an existing SignalR test covers commands over the hub;
- a generated-command round trip in IntegrationTests.

## Context

Scry is read-only by design: generated client query models, captured LINQ, a restricted wire AST, and a server that re-validates everything against a default-deny allow-list. Since `3cc464e` a query can also be **live**, answered again whenever its answer changes (`docs/live-queries.md`). The docs still state the read-only position: `docs/comparisons.md:57`, `:130`, `:158`, `:213`; `docs/linq-coverage.md:208`; `docs/attachments.md:194`.

This round adds writes as **commands**, in the service-bus sense. A command is a message class the client sends and the server validates, authorizes and dispatches. One decided within a short window answers at once. One that is not answers `Pending` and streams its outcome on the same response, so the UI behaves synchronously when the server is idle and otherwise hands the work to a pending-work panel. What a command wrote reaches every screen, the sender's and everyone else's, through the live queries that read it. A command may be handled in-process or routed through NServiceBus, MassTransit, Rebus or Wolverine, and an existing bus message class can be exposed to the client by annotating it.

Decisions (first draft, with what live queries changed):
- Push: **Server-Sent Events**, in-box in .NET 10 (`System.Net.ServerSentEvents` at both ends). SignalR only as the optional `Scry.Server.SignalR` / `Scry.Client.SignalR` transport that live queries shipped. *Revised:* the outcome streams on the command's own response in the live-query framing, not over a separate per-client channel.
- "Can the user run X?": a **type-level bool** on the generated facade (`Query.Commands.CanDeleteEmployee`, from a capabilities endpoint) **plus a per-row bool** on the target's query model (`EmployeeQueryModel.CanDeleteEmployee`), computed server-side in SQL from the command's policy. Inside a live query it is re-decided on every run.
- Other users' work: *revised.* What they complete arrives through live queries. What they have in flight is not shown this round.
- Bus adapters: **NServiceBus 10.2.9** (into the existing `Scry.Server.NServiceBus`), **MassTransit 8.5.10** (the last Apache-2.0 line; 9.x is commercial), **Rebus 8.9.3**, **WolverineFx 6.33.0**. All target net10.0 (nuspecs checked).

Governing constraint, unchanged: the client is hostile. Every command is re-validated and re-authorized server-side, and nothing about hidden rows leaks. The standard is the closed security review in `src/todo.md` (RequireJson, bounded messages, `EnsureDefined` on wire enums, non-null array elements, wire names only in rejections, startup resolvability) plus the live-query section of `docs/security.md`.

## What live queries already provide

| The first draft built | Now |
| --- | --- |
| refetch on a non-`Pending` outcome, on `Completion` and on `ActivityChanged`; `.WithHeader("Cache-Control","no-cache")` re-reads past Delta's lag | the page's query is `.Live()`; a handler's save reaches it through `ScryChangeInterceptor`, a worker's through the NServiceBus backplane, the rest through the change probe or the poll |
| `{p}/events` (per-client SSE, `Scry-Client` header, `Last-Event-ID` replay ring, `EventHeartbeat`, `EventReplay`) and `GET {p}/command/{id}` polling | a pending receipt streams on its own response, framed as `{p}/subscribe` frames answers (a shared `EventStream`); a client re-attaches by id |
| `ScryChannel` (reconnect, backoff, `ChannelState`, `ReceiptPollInterval`, `MaxReconnectDelay`, `ConnectChannelEagerly`) | `ScryClient.Reconnect` (`IScryRetryPolicy`) and the `SseParser` reading `LiveAsync` does |
| `{p}/watch`, `WatchRequest`, `.Watched()`, `PlanWatch`, `MaxWatchedRows`, composite-key OR-of-ANDs | nothing: refresh is the live query's job, and in-flight activity is out of scope |
| `CommandEvent` / `CommandCompletedEvent` / `CommandActivityEvent` / `ActivityPhase` | `CommandReceipt`, sent as `result` events |
| `ScryActivity`, `ScryActivityTracker`, `ScryActivityEntry`, `ActivityLinger` | nothing |
| custom-transport clients refuse commands | `ScryProcessor.SendCommand` as the seam, hub methods, `ScryClient` delegates beside `subscribeTransport` |
| sidecar `Channel` kind | `Subscription` exists; add `Command`, headers-only when the answer is an event stream (a check the handler already makes) |
| the sample's `CreateEmployee` routed through Rebus in-memory | `Sample.NServiceBusServer` + `Sample.NServiceBusWorker` already send `RepriceOrder` to a worker; it becomes the annotated command |
| multi-node tracker backplane (out of scope) | still out: receipts carry results, and `IScryChangeBackplane` promises to carry entity names only |

## The design in one page

### Authoring (server model or a messages assembly)

```cs
[Command(typeof(Employee))]                       // targeted: acts on one Employee row
public class DeleteEmployee { public int Id { get; set; } }

[Command(typeof(Employee))]
public class RenameEmployee { public int Id { get; set; } public string Name { get; set; } = ""; }

[Command(Result = typeof(EmployeeCreated))]       // untargeted, with a typed result
public class CreateEmployee { public string Name { get; set; } = ""; public int DepartmentId { get; set; } public Status Status { get; set; } }
public class EmployeeCreated { public int Id { get; set; } }
```

- `[Command]` (`Scry.Annotations`, `AttributeTargets.Class`): ctor `()` / `(Type target)`; `Name` override (blank = unset); `Result` type; `Policy` type (server-only, like `[ReturnableWith]`; the generator ignores it).
- `[CommandIgnore]` hides a payload property (the server fills it). `[QueryIgnore]` on a command property is a generator diagnostic and a server startup error.
- Payload = public get+set instance properties: scalar, enum, `byte[]`, or nullable of those, plus lists and nested plain DTOs of the same, declared in a command assembly. Navigations, collections of entities, entities, `object`, `JsonElement` and dictionaries are refused.
- **Target key binding**: a targeted command carries the target's key as a property named like the key member (`Id`) or `{TargetClrName}{Key}` (`EmployeeId`), derived identically by `MetadataModelReader.Keys` and `Schema.DeriveKeys`. Unbound → SCRY010 + startup exception. Consequently `Keys` are populated (and `~keys` stamped, `[ScryModel(Keys=…)]` emitted) for any type that is a command target, extending today's attachment-only gate.
- Commands may live in the model assembly (scanned by default) or in extra assemblies: MSBuild `ScryCommandDll` (`;`-separated) for the generator, `options.AddCommands(Assembly)` on the server.
- Handlers: `ICommandHandler<T>` / `ICommandHandler<T,TResult>` registered in plain DI (`services.AddScoped<ICommandHandler<DeleteEmployee>, DeleteEmployeeHandler>()`), or a bus adapter that claims the command. When commands are on, startup refuses a command with no route or with two routes (default-deny).
- Policies: `ICommandPolicy<TCommand>.Allow(ScryPolicyContext)` (type-level) and `ICommandPolicy<TCommand,TEntity>.Rows(ScryPolicyContext) : Expression<Func<TEntity,bool>>` (row-level), registered via `[Command(Policy=…)]` or `options.AddCommandPolicy<TCommand,TPolicy>()` (options win). No policy → allowed. A targeted command's row must also be visible through the source's `IReturnablePolicy` chain.
- `samples/Sample.Model/Messages/RepriceOrder` is a message two NServiceBus endpoints already share, recognised by convention so the model has no NServiceBus reference. It gains `[Command(typeof(Order))]`: the existing-bus-message case, with no new dependency (`Sample.Model` already references `Scry.Annotations`).

### Generated client surface (`Scry.Generated`, `ScryCommands.g.cs`)

```cs
[global::Scry.ScryCommand("DeleteEmployee", Target = "Employee", Keys = new[] {"Id"})]
public sealed class DeleteEmployee { public int Id { get; init; } }
[global::Scry.ScryCommand("CreateEmployee", Result = typeof(global::Scry.Generated.EmployeeCreated))]
public sealed class CreateEmployee { … }
public sealed class EmployeeCreated { public int Id { get; init; } }

public sealed class ScryCommands
{
    public ScryCommands(global::Scry.ScryClient client) => this.client = client;
    public global::System.Threading.Tasks.Task<global::Scry.ScryCommandOutcome> DeleteEmployee(
        global::Scry.Generated.DeleteEmployee command,
        global::System.Threading.CancellationToken cancel = default) =>
        client.SendCommandAsync(command, cancel);
    /// Whether this caller may send 'DeleteEmployee' at all.
    public bool CanDeleteEmployee => client.Can("DeleteEmployee");
    public global::System.Threading.Tasks.Task<global::Scry.ScryCommandOutcome<global::Scry.Generated.EmployeeCreated>> CreateEmployee(…) =>
        client.SendCommandAsync<global::Scry.Generated.CreateEmployee, global::Scry.Generated.EmployeeCreated>(command, cancel);
    …
}
// ScryQuery gains: public ScryCommands Commands { get; }  (only when the model has commands)
// EmployeeQueryModel gains: public bool CanDeleteEmployee { get; init; }  public bool CanRenameEmployee { get; init; }
//   — listed in [ScryModel(...)] members and the default projection; stamped as ordinary (name, "bool") members.
```

Rules: commands ordered by name; types fully qualified (the method/type homonym `DeleteEmployee(DeleteEmployee)`); `CancellationToken` spelled in full; Task-returning plain instance methods (F#-safe). Nothing command-related is emitted when the model has no commands.

### Wire (`Scry.Wire`, camelCase, all roots in `WireJsonContext`, requests in `ScryJson.IsRequest`)

```cs
public sealed record CommandRequest(int Version, string Command, Guid Id, JsonElement Payload) { const int CurrentVersion = 1; string? Stamp; static Create(...) }
public enum CommandStatus { Pending, Completed, Failed }
public sealed record CommandReceipt(int Version, Guid Id, CommandStatus Status) { JsonElement? Result; string? Error; string? Stamp }   // optional members as init props: the writer omits nulls and RequireWhatTheWriterAlwaysWrites would require positional ones
public sealed record CommandCapabilities(int Version, IReadOnlyList<string> Commands) { string? Stamp }
public static class ScryCommandProtocol { const string Route = "command"; const string CapabilitiesRoute = "capabilities"; }
```

- **The streamed receipt is a live answer.** The event names and `ScryLiveEnd` are `ScryLive`'s:
  - `result`: data is a `CommandReceipt`, `Pending` first and then the final one, after which the server closes the stream.
  - `ping`: sent on the fixed clock.
  - `error`: data is a `ScryError`.
  - `end`: `reconnect: true`, with reason `lifetime`, `expired` or `shutdown`.

  There is no `id:` and no `Last-Event-ID`, because re-attaching answers the current receipt, which is already idempotent. A stream that closes before a final receipt with neither `error` nor `end` was cut. Every event carries a `data:` line.
- `ScryErrorCode` gains `NotFound` (404), `PayloadTooLarge` (413) and `CommandLimit` (503, or 429 for the caller's share). The hub has no status line, so every status a command can be refused with needs a code, and `ResponseFailure.Status` maps each back.
- `ScryHubProtocol` gains `Command`, `Receipt` and `Capabilities`.
- `ScryIntrospection.CurrentVersion = 2` (still 1 on `main`). It gains `IReadOnlyList<ScryCommandInfo> Commands` (`Name`, `Target` model name, `Keys`, `Properties` as `ScryMemberInfo`s, `Result: ScryResultInfo?`, `Obsolete`). `ScryMemberInfo` gains `IsCapability` + `Command`, both omitted when default.
- `ScryPermissionException.CommandDeniedMessage = "The command was denied by a server policy."`.
- `NonNullElementsConverterFactory` becomes `public`, because the server's binder reuses it for payload lists.
- Schema stamp: `src/Shared/SchemaStamp.cs` header `scry-schema-v3` (v2 on `main`). `Compute` gains `commands` (`command {Name}[ : {TargetModel}]` + members + synthetic `~keys`/`~result`) and `results` sections. Both `StampMembers` mirrors stay unchanged (capabilities flow as ordinary members). The new `StampCommandMembers` pair is kept byte-identical.

### HTTP surface (inside `MapScry(pattern)`'s `Endpoints` aggregate, so `.RequireAuthorization()` covers them; mapped only where `MaxPendingCommands > 0`)

| Route | Method | Body → answer |
| --- | --- | --- |
| `{p}/command` | POST | `CommandRequest` → a `CommandReceipt` (200 JSON, `no-store`) when it is decided within `CommandSyncWindow`; otherwise 200 `text/event-stream`, as above |
| `{p}/command/{id:guid}` | GET | the same answer for a command this server holds, only for the `Caller` that sent it; otherwise 404, byte-identical for unknown and not-yours. What a client re-attaches with |
| `{p}/capabilities` | GET | `CommandCapabilities` (type-level policy per command for this caller), `no-store` |

Failure table for `POST /command` (all `ScryError` bodies with a code, `no-store`):
- 415 for a non-JSON body.
- 413 `PayloadTooLarge` for a body over `MaxCommandBytes`, checked before reading.
- 400 for wire, version, unknown command or payload problems (unmapped member, `$type`, wrong type, missing required or key, null element, unknown enum name, depth, DataAnnotations, duplicate id). Messages name only wire names, plus the stale suffix when drifted.
- 403 with a fixed message for a type-level denial.
- **404 `NotFound` `"The command's target was not found."` for absent, hidden and row-denied targets alike**: one query, one answer, as attachments do.
- 503 `CommandLimit` at `MaxPendingCommands`, and 429 at `MaxPendingCommandsPerCaller`, both with `Retry-After`.
- 500 with the fixed `"Command dispatch failed."`.

Every refusal is decided before the response is committed, the way `HandleSubscribe` decides its first answer. With commands off there is no route: a 404 with no Scry body, which the client reports the way it reports a server that is not serving live queries.

### Runtime flow

1. Bind the payload into the **server's** command type through a per-command restricted `JsonTypeInfo` (allow-listed properties only, `UnmappedMemberHandling.Disallow`, `PolymorphismOptions = null`, enums by name only, `MaxDepth = 16`, `RespectNullableAnnotations`), then `Validator.TryValidateObject`.
2. A type-level policy denial → 403. Targeted commands: `ResolveSource(target)` (policy chain, cached policies refreshed) + the key predicate (`Parameterization.Parameterize`, lifted from `QueryExecutor.PlanAttachment` into a shared `KeyPredicate` helper) + the row policy expression → `Any()`; false → 404.
3. The processor's limits: `MaxPendingCommands`, then `MaxPendingCommandsPerCaller` by `Caller`.
4. `tracker.Accept(record)` **before** `dispatcher.Dispatch(envelope)`.
5. `ScryProcessor.SendCommand` yields the final receipt if the command is decided within `CommandSyncWindow`, otherwise `Pending` and then the final one. Every refusal above throws from the first `MoveNextAsync`, as `Subscribe` does, so each transport answers it with its own status or coded error. A request abort stops the wait, never the command.
6. The in-process dispatcher runs the handler on a background task with its own DI scope and `DbContext`. It calls `SaveChangesAsync` when `context.SaveChanges` (default true) and the tracker has changes, serializes a typed result with `ScryJson.Options`, then calls `Complete`/`Fail`. `ScryCommandException` carries a client-visible message. Anything else fails with the fixed `"Command execution failed."` and the real message goes to the audit trail. The context is the host's `AddDbContext` registration, so where the host added `ScryChangeInterceptor` (as the sample does) the save re-asks the live queries that read what it wrote. Nothing about the command itself reaches them.
7. On `Completed`, a targeted command's pipeline calls `ScryChanges.Notify(target)`. That covers an `ExecuteDelete`/`ExecuteUpdate` handler and a bus worker that reports nothing. Where the interceptor reported too, the lease's due flag makes the two one run.
8. Client: mints `Id` and POSTs. A JSON answer is the outcome. An event stream is read until the final receipt or `CommandWait` (default 3 s from send). Past that the outcome is `Pending`, with a `ScryPendingCommand` in `ScryPendingWorkStore` whose `Completion` is fed by the same stream. A cut or an `end` re-attaches through `GET {p}/command/{id}`, timed by `ScryClient.Reconnect`. A 404 there ends it `Unknown`: the server no longer holds the outcome, and the command may have run. A 403 refreshes capabilities.

### Capability member (`MemberKind.Capability`)

For each targeted command X on E, `E`'s `TypeMeta` (and opted-in subclasses) gets `Member.Capability("X")`: `Name = "CanX"`, `Type = bool`, `Property = null`.

Rebinding happens in `ExpressionBuilder.BuildMemberAccess` and the two default-projection loops:
- Type-level policy denies → `Expression.Constant(false)`.
- Row policy registered → its lambda body, with the parameter replaced by the owner expression and every scalar `ConstantExpression` parameterized.
- Otherwise → `true`.

It is computed once per call (`CapabilityContext` created in `QueryExecutor.Walk`, memoized per command). It is valid in `Where`, `Select`, nested navigation paths, `OrderBy`, `GroupBy`, `Distinct` and subqueries, and is not traversable. `NavigationPolicyProbe` and `PlanAttachment` pass no context, so it reads as `false` and nothing runs. Every `Member.Property` dereference gates on kind or null. Since the refactors after the first draft, that is ten sites in four files: `ExpressionBuilder.cs:135/337/1145`, `NavigationPolicyProbe.cs:116`, `QueryExecutor.cs:1004/1015`, `Schema.cs:306/1081/1147/1431`.

**Inside a live query** each run is a call, so the member is decided again on every run. A row-policy `Can*` flips on screen when its row changes. A type-level decision that reads a claim reaches the screen within `SubscriptionPollInterval` or `SubscriptionLifetime`, the bound `docs/security.md#live-queries` already gives row policies. The row policy's lambda is inlined into the composed query, so `DependencyWalker` counts a table that only a command policy reads among what the live query listens for.

## Commits

Base: `main` at `3cc464e`. Each commit leaves `dotnet build src/Scry.slnx`, `dotnet test src/Scry.slnx`, `dotnet test samples/Scry.Samples.slnx` and `dotnet test IntegrationTests/IntegrationTests.slnx` green, run sequentially (never two trees at once). Snapshots that legitimately move are listed per commit; inspect received-vs-verified before accepting. No commit is made unprompted.

### 1. Contract and wire

- `src/Scry.Annotations/CommandAttribute.cs`, `CommandIgnoreAttribute.cs`. Nothing else goes here. The bus completion contract and header names the first draft put in Annotations belong to the bus packages and `Scry.Server`: a worker already references `Scry.Server.NServiceBus` for change reporting, and `ScryChanged` set the pattern of a bus message carrying a `ScryJson` string. Annotations targets net10.0 and stays dependency-free.
- `src/Scry.Wire`:
  - `CommandRequest.cs`, `CommandReceipt.cs`, `CommandStatus.cs`, `CommandCapabilities.cs`, `ScryCommandProtocol.cs`.
  - `ScryHubProtocol` + `Command`/`Receipt`/`Capabilities`; `ScryErrorCode` + `NotFound`/`PayloadTooLarge`/`CommandLimit`.
  - `Introspection/ScryCommandInfo.cs`, `ScryResultInfo.cs`, the `ScryMemberInfo` flags, `ScryIntrospection` v2 + `Commands`.
  - `WireJsonContext` roots; `ScryJson` (`IsRequest`, cached `JsonTypeInfo`s, `Serialize`/`Deserialize` helpers for each new root).
  - `ScryPermissionException.CommandDeniedMessage`; `NonNullElementsConverterFactory` made public.
- `src/Scry.Client/ResponseFailure.cs`: `Status` maps the three codes (`NotFound` → 404, `PayloadTooLarge` → 413, `CommandLimit` → 503).
- Tests: `WireMetadataTests` (the sweep picks up the records); `WireSerializationTests.CommandRequestRoundTrips` / `CommandReceiptRoundTrips` / `CommandCapabilitiesRoundTrip` / `ACommandRequestRefusesAnUnknownMember`; the code mapping, beside `SubscriptionLimit`'s.
- Moves: `IntrospectionTests.Describe.verified.txt` (`Version: 2`, empty `Commands`) and its doc embed; `UiSnapshotTests.ExplorerIntrospectionEndpoint.verified.txt`.

### 2. Generator, schema and capability rebinding (the static surface)

Generator (`src/Scry.SourceGenerator`):
- `SignatureDecoder.cs`: `IsSystemType` must return true for `System.Type`. It still returns false on `main` (`SignatureDecoder.cs:118`), so a `typeof(...)` fixed argument is misparsed as an enum. `GetSystemType` → `NamedDecoded("System.Type")`; `GetTypeFromSerializedName` → new `SerializedTypeDecoded(string? Name)`. Strip an assembly qualification at the first `,`, since a type from another assembly is written assembly-qualified.
- `ModelExtract.cs`: `CommandInfo(Name, ClrName, TargetClrName, TargetOptedIn, TargetKind, TargetSourceName, TargetModelName, Keys, KeyProperties, Properties, QueryIgnored, ResultName, ResultProblem, Obsolete)`, `ResultInfo(Name, FullName, Properties)`. `ModelExtract` gains `Commands`, `Results`, `CapabilityCollisions`; `PropertyInfo` gains a trailing `string? Capability = null`.
- `MetadataModelReader.cs`:
  - consts `Scry.CommandAttribute` / `Scry.CommandIgnoreAttribute`; `Read(dllPath, IReadOnlyList<string>? commandDllPaths)` opens every reader.
  - `TryClassify` counts `[Command]` as an opt-in for the SCRY008 conflict without making the type a source.
  - `ReadCommands`: public get+set, no indexer/static, skip `[CommandIgnore]`, record `[QueryIgnore]`, walk bases within the opened readers, and `Classify` with an enum lookup across all opened readers, for command and result properties only. Plus the result DTO read.
  - Capability injection (`Can{Name}`, `"bool"`, `Capability: Name`) into the target's `Properties`, before `WithoutInheritedMembers`/`DeriveKeys`. Key gate `IsAttachment || Capability is not null`.
  - Key binding: `k` / `{Target}{k}`, the type must match, ambiguity recorded.
- `ScryGenerator.cs`:
  - `Initialize` reads `build_property.ScryCommandDll`, split on `;` and `|`.
  - Diagnostics: SCRY009 target not a queryable entity; SCRY010 key not bound; SCRY011 duplicate name (across commands, sources, models, enums, results, `ScryQuery`/`ScryCommands`); SCRY012 not an identifier; SCRY013 `[QueryIgnore]` on a command property; SCRY014 non-scalar property; SCRY015 bad result DTO; SCRY016 `Can{X}` collides with a member; SCRY017 not a concrete class. Extend SCRY008's text with `[Command]`, and add rows to `AnalyzerReleases.Unshipped.md`.
  - `EmitCommands` → `ScryCommands.g.cs`. `EmitQuery` adds `Commands` when there are any. `Arguments` emits `Keys = new[]{…}` whenever `source.Keys.Length > 0`. `ComputeStamp` passes commands and results. Invalid → emit nothing (today's policy).
- `src/Shared/SchemaStamp.cs`: v3 as above.
- `src/Scry.Client/buildTransitive/Scry.Client.targets`, plus the inline copies in `samples/Sample.WebClient`, `Sample.QueryModels`, `Sample.ConsoleClient`, `Sample.WpfClient`, `Sample.WinFormsClient` and `IntegrationTests` (six since the desktop samples):
  - `_ScryCommandDll` items normalized to full paths, and `CompilerVisibleProperty ScryCommandDll`.
  - `ComputeScryStamp` hashes `$(ScryModelDll);@(_ScryCommandDll)` via `GetFileHash`'s `Items` output, joined into one `ScryModelStamp`.
  - An `EnsureScryCommands` error.
  - Unverified: whether the editorconfig writer keeps `;` inside the value. After the first two-DLL build, inspect `obj/**/*.GeneratedMSBuildEditorConfig.editorconfig` and switch the join to `|` if it is truncated.
- `src/Scry.Client/ScryCommandAttribute.cs` (`Name`, `Target`, `Keys`, `Result`), so the emitted attribute resolves; `ScryModelAttribute.Keys` doc updated. The rest of the client lands in commit 5, and nothing consumes the facade until commit 8.
- Tests: `GeneratorTests.Commands` (the three sample-shaped commands: the full `ScryCommands.g.cs` + `EmployeeQueryModel` with two capabilities + `ScryQuery.Commands`), `CommandInAnotherAssembly` (two DLLs), `CommandNameOverride`, `CommandWithTypeNameKey` (`EmployeeId`), `CommandObsolete`, and one `[TestCase]` per diagnostic (SCRY009–017, and SCRY008 with `[Command]`).

Server schema (`src/Scry.Server`):
- `MemberKind.Capability`. `Member` takes `PropertyInfo?` plus `Command`; `static Member.Capability(string command)`; `Sensitive`/`BinaryTransfer`/`ContentType`/`Element`/`Target` made null-safe. `TypeMeta.AttachmentKeys` → `Keys` (8 usages).
- `CommandMeta.cs` (internal): `Name`, `ClrType`, `Target: ScrySource?`, `TargetKeys: (Member Key, PropertyInfo Payload)[]`, `Payload: PropertyInfo[]`, `Result`, `Policy`, `Capability: Member?`, `Available`, `TypeInfo: JsonTypeInfo` (built by commit 4's binder; leave a hook).
- `Schema.cs`: a `commands` dictionary, `TryGetCommand`, `Commands`, `CommandsTargeting(Type)`. A `CommandCatalog.Build` pass runs after pass 2:
  - Scan `contextType.Assembly ∪ options.CommandAssemblies` for `[Command]` (`inherit: false`). `EnsureOneOptIn` counts it.
  - Refuse abstract, generic, non-class, and no public parameterless ctor. The name must be an identifier, with collisions reported in SCRY011's wording.
  - The target must be an opted-in `Entity` source.
  - Payload rules: `[QueryIgnore]` → an error pointing at `[CommandIgnore]`; an enum must live in the model or a command assembly.
  - Key binding via `DeriveKeys` (rename `DeriveAttachmentKeys` → `DeriveRequiredKeys(meta, what)`, and `ValidateAttachmentKeys` → `ValidateKeys`).
  - Result DTO rules.
  - Policy shape: a `CommandPolicy.Describe` reflection cache like `AttachmentPolicy`, taking `ICommandPolicy<TCommand>` for this command, and the row interface only on a targeted command whose target derives from `TEntity`.
  - `[CommandIgnore]` on a query member → error.
  - Capability members added to the target and opted-in subclasses (collision → error).

  The keys gate in pass 2b becomes `Attachment or Capability`.
- `ScryOptions`: `AddCommands(Assembly)`, `AddCommandPolicy<TCommand,TPolicy>()`, `CommandPolicies`, `CommandAssemblies`.
- `Describe`/`DescribeSurface`: a capability → `ScryMemberInfo(name, "bool", false, false) { IsCapability = true, Command }` (skip `ObsoleteOf(member.Property)`). A `Commands` list ordered by name; `ComputeStamp` with commands and results; the `StampCommandMembers` mirror.
- Capability rebinding:
  - `CapabilityContext.cs` (internal): `Evaluate(member, owner, ownerType)`, `RowsOf(CommandMeta)` memoized, and the `ConstantParameterizer` and `ParameterReplacer` visitors.
  - `ExpressionBuilder` gets a fifth ctor parameter, the `BuildMemberAccess` branch before the navigation branch, and the two default-projection loops (`Kind is Scalar or Capability`).
  - `QueryValidator.ResolvePath` `requireScalar` accepts `Capability`, and `QueryExecutor.Walk` constructs the context.
  - Guards at the ten `Member.Property` sites listed above.
- Startup: `EnsurePoliciesResolvable` covers command policies, and `ProbePoliciedNavigations` also translates every registered `Rows` lambda once (`Set<E>().Where(rows).Any()` via `ToQueryString`). Both run from `ScryProcessor.EnsureReady`, which `MapScry` and `MapScryHub` share, so a hub-only host is held to them.
- `TestModel.cs` additions (keep the `Employee` snapshots still):
  - `[Command(typeof(Contract), Name = "SealContract")] Seal { ContractId, [CommandIgnore] SealedBy }`
  - `[Command(typeof(Shift))] [Obsolete] RenameShift { Id, Name }`
  - `[Command(Result = typeof(ShiftCreated))] CreateShift { Name, Day, Perks }` and `ShiftCreated { Id }`
  - a composite-key `Assignment { [Key] EmployeeId, [Key] ProjectId }` with a targeted command (composite-key binding)
- Tests:
  - `LockstepTests`: stamp, members, and a new `GeneratorAndServerAgreeOnEveryCommand`.
  - `IntrospectionTests`: `CommandsAreDescribedWithTargetKeysAndResult`, `CapabilityMembersAreOrdinaryBools`, `ATargetedTypeGainsKeys`.
  - New `CommandSchemaTests`: dynamic assemblies via `AssemblyBuilder` + `AddCommands`, one test per startup refusal, `BindsTheTypeNameKeyConvention`, `ScansAddedAssemblies`.
  - New `CapabilityMemberTests`:
    - the default projection carries it;
    - `Where`/`Select`/`OrderBy`/`GroupBy`/`Distinct`/subquery, through a navigation, through a null navigation, and through a policied navigation;
    - a type-level denial reads false everywhere, and no policy reads true;
    - decided once per call; the SQL preview shows the row expression; constants inside are parameters;
    - traversal refused; never a key, never sensitive or binary;
    - `ALiveQueryAnswersAgainWhenACapabilityFlips` and `ALiveQueryListensToATableOnlyACommandPolicyReads`, over the in-process `subscribeTransport` as `LiveQueryRoundTripTests` does.

Explorer:
- `ModelSynthesizer` emits command DTOs, result DTOs and a `ScryCommands` facade (`=> null!` bodies) plus `ScryQuery.Commands`, in non-executable mode only. `Arguments` emits `Keys` when present.
- `SchemaIndex`: `Commands`, `Command(name)`, `CommandsTargeting(model)`, and search matches command names (add `Command` to `SchemaMatch`).
- `SchemaPane.razor`: a `command` badge on capability members, a commands list on the type page, and a "Commands" section on the root page.
- Tests: `RoslynLayerTests.CompletesCapabilityMembers` / `CompletesCommandsOffTheFacade` / `SynthesizedCommandsCompileAgainstScryClient` / `ExecutableModelOmitsCommands`; `SchemaIndexTests.ListsCommandsTargetingAModel` / `SearchesCommandNames`.

Moves:
- All 10 `GeneratorTests.*#ScryQuery.g.verified.cs` (stamp only).
- `IntrospectionTests.Describe.verified.txt` (commands, `Shift`/`Contract` capabilities and keys, new stamp).
- The `docs/*` snippet regions `buildTransitiveProps` and `clientGeneratorWiring`.
- `UiSnapshotTests.ExplorerIntrospectionEndpoint.verified.txt`.
- `UiScreenshotTests.ExplorerSchemaPane.verified.png`: the badges appear only once the sample model has commands, in commit 8.

Check, do not blindly accept, any snapshot of a whole-model query over `Shift`/`Contract`.

### 3. Share the event-stream framing (no behaviour change)

- Move what `ScryServiceExtensions.Subscribe.cs` does that is not about queries into an internal `EventStream` in the global namespace:
  - `WriteEvent`;
  - `Commit`: `no-store`, `X-Accel-Buffering: no`, `Content-Encoding: identity`, buffering off;
  - the fixed-clock heartbeat;
  - `Deadline`: `SubscriptionLifetime` or the ticket's expiry;
  - `End` and `Fail`;
  - the `LiveAnswers` cancel-then-dispose discipline.
- `HandleSubscribe` keeps its bytes. `SubscriptionHttpTests` and `SignalRTests` are the guard, and no snapshot moves.

### 4. Server pipeline, tracker and endpoints

Public (`namespace Scry`, `src/Scry.Server`):
- Handlers and policies: `ICommandHandler<T>` (`Task Handle(T, ScryCommandContext, Cancel)`) / `ICommandHandler<T,TResult>` (`Task<TResult> Handle(...)`); `ICommandPolicy<T>` / `ICommandPolicy<T,TEntity>`; `ScryCommandContext(services, db, commandId, commandName, caller, target keys) { bool SaveChanges = true }`.
- Dispatch: `ICommandDispatcher { bool CanDispatch(Type); Task Dispatch(CommandEnvelope, Cancel) }`; `CommandEnvelope(Id, Name, CommandType, Command, Caller, Source, Keys, ResultType)`.
- Tracking: `ICommandTracker` (`Accept`, `Completion(id)`, `Complete`, `Fail`, `Find(id, caller)`), `CommandRecord`, and `MemoryCommandTracker` (the public default).
- `ScryCommandHeaders` (`scry-command-id`, `scry-caller`), for the bus packages.
- Exceptions: `ScryCommandException` (client-visible message, bounded to 1024); `ScryCommandLimitException` (`PerCaller`, as `ScrySubscriptionLimitException`).
- Audit: `ScryAuditEntry.Command` + `CommandStatus` (a fourth discriminant; `Subscribed` stays false). A `Pending` answer writes a second entry on completion, from a scope created via `IServiceScopeFactory`.
- `ScryOptions`:
  - `MaxPendingCommands`, default **0: no routes, every capability reads false, and no command needs a route** (the `MaxSubscriptions` convention).
  - `MaxPendingCommandsPerCaller` (20), `CommandSyncWindow` (1 s), `CommandRetention` (5 min), `MaxCommandBytes` (64 KiB).
  - `AddDispatcher<T>()`: registration order = precedence; adapters beat in-process.
  - Ranges are validated in `Schema.Build` when commands are on.
  - **`SubscriptionCaller` becomes `Caller`**: one identity for the live-query and pending-command limits, re-attach, `ScryCommandContext`, the audit and `scry-caller`. The package is 0.1.0-beta.9, and the `scryOptionsSubscriptions` snippet and the security checklist move with it.
  - A pending receipt's stream is held to `SubscriptionHeartbeat` and `SubscriptionLifetime`, as a live query's is.
- `ScryProcessor`:
  - `SendCommand(request, db, services, requestHeaders, caller, cancel) : IAsyncEnumerable<CommandReceipt>` is the seam, with the limits here so every transport has them.
  - `Receipt(id, caller, cancel) : IAsyncEnumerable<CommandReceipt>` and `Capabilities(services, requestHeaders) : CommandCapabilities`, with header-less overloads using `EmptyServiceProvider`.
  - `EnsureReady` gains `EnsureCommandTargetsMapped(db)` (joins `EnsureSourcesMapped`; `AllowUnmappedSources` marks the command unavailable) and `EnsureCommandsDispatchable(services)` (every available command routes to exactly one dispatcher; in-process needs exactly one handler whose arity matches `Result`; a message per case). Both run only when commands are on.
- `AddScry`: `TryAddSingleton<ICommandTracker, MemoryCommandTracker>()`, `AddSingleton<InProcessDispatcher>()`.

Internal:
- `CommandBinder`: restricted `JsonSerializerOptions` per schema; `Bind(meta, payload)`; STJ messages rewritten to wire names.
- `CommandPolicy`: a reflection cache.
- `CommandPipeline`: accept → dispatch → sync wait. Its `Complete`/`Fail` hooks record `scry.server.command.handling.duration`, write audit #2, and notify the target on completion.
- `CommandRouter`.
- `InProcessDispatcher`: `Task.Run`, its own scope, compiled `HandlerInvoker` delegates, and `IHostApplicationLifetime.ApplicationStopping` as the only cancel. Plus `HandlerInvoker`.
- `CommandRecorder`: activity `scry.command {name}`; histogram `scry.server.command.duration` with `scry.outcome ∈ completed|pending|failed|rejected|denied|malformed`; up-down counter `scry.server.commands.pending` beside `scry.server.subscriptions.active`.
- `KeyPredicate`, shared with `PlanAttachment`.

`MemoryCommandTracker`:
- One lock, and records holding a `TaskCompletionSource<CommandReceipt>` (`RunContinuationsAsynchronously`) and the caller.
- Per-caller pending counts.
- A `PeriodicTimer` prunes completed records past `CommandRetention` and fails pending ones past `12 × CommandRetention` ("No completion was received.").
- `internal Prune(DateTimeOffset)` for tests.
- No client ids, watches, replay ring or per-client channels.

Endpoints live in `ScryServiceExtensions.Commands.cs` (partial, like `.Subscribe.cs`): `HandleCommand`, `HandleReceipt`, `HandleCapabilities`.
- Each calls `Advertise` first.
- The POST uses `RequireJson` and `ReadBoundedBody(context, MaxCommandBytes)`.
- The first receipt is decided before the response is committed. A final one answers JSON, and a `Pending` one hands over to `EventStream`.

Docs snippets to add now (prose in commit 11): the `commandHandlerInterface`, `commandPolicyInterface` and `scryOptionsCommands` regions in the new sources.

Tests (`src/Scry.Tests`, isolated DB `CreateIsolated("Commands")` where writing):
- `CommandBindingTests`.
- `CommandPolicyTests`, including `ARowDenialIsIndistinguishableFromAMissingRow` and `ConstantsInTheRowExpressionAreParameters`.
- `CommandTrackerTests`.
- `CommandStartupTests`: every refusal in the router/dispatchability list, and commands off asking for no route.
- `CommandPipelineTests`: inline vs pending, saves changes, opt-out, `ScryCommandException` visible, other exceptions opaque, own scope, abort does not cancel, shutdown fails in-flight, `ACompletedTargetedCommandNotifiesItsTarget`, `AFailedCommandNotifiesNothing`.
- `CommandObservabilityTests`.
- `CommandLiveQueryTests`: one processor, with the in-process `subscribeTransport` and `commandTransport`. A live query answers again after a command's save, after an `ExecuteDelete` handler, and within `SubscriptionThrottle`. The target notify beside the interceptor costs at most one extra run.

`IntegrationTests/CommandHttpTests.cs` uses a self-contained `CommandContext` like `AttachmentTests`, with raw JSON bodies over `TestServer`, reading events as `SubscriptionHttpTests` does. It covers:
- inline JSON, and pending then the final receipt;
- re-attach while pending and after completion, re-attach by another caller byte-identical to an unknown id, and `end` at lifetime;
- every 400 shape, 413, 415, 403, and a hidden-and-missing byte-identical 404;
- 503 and 429 with `Retry-After`, and commands off mapping nothing;
- capabilities and the capability member per identity, via a test `AuthenticationHandler`;
- `RequireAuthorization` reaching all three routes, the stamp on every status, and a drifted client being told so;
- a `/subscribe` stream on the same server receiving the write.

### 5. Client

`src/Scry.Client` (`namespace Scry` unless noted):
- Settings are properties on `ScryClient`, as `Reconnect` is: `CommandWait` (3 s), plus `ScryPendingWorkStore.CompletedLinger` (8 s). There is no `ScryClientOptions` type, and no new `ForHttp` or `AddScryClient` overloads. The store is registered scoped **off the client** (`_.GetRequiredService<ScryClient>().PendingWork`) so it shares the client's lifetime (`AddScryClient` is scoped for the drift-once reason).
- New types:
  - `ScryCommandModels`: an internal cache over `[ScryCommand]`, with `Keys(command, model)` via `ValueTag.Of`.
  - `ScryCommandStatus`: `Pending`, `Completed`, `Failed`, `Unknown`.
  - `ScryCommandOutcome`: `Command`, `Id`, `Status`, `Result`, `Error`, `Pending`, `Completion`, `EnsureCompleted()`.
  - `ScryCommandOutcome<TResult>`: `Value` via `element.Deserialize<TResult>(ScryJson.Options)`, and a typed `Completion`.
  - `ScryCommandFailedException`, `ScryPendingCommand`.
  - `ScryPendingWorkStore`: `Items`, `PendingCount`, `ClearFinished()`, and `Changed`, raised on the synchronization context `SendCommandAsync` was called on, the way `Subscribe` delivers.
- `ScryClient` members:
  - `PendingWork`, `Ready`, and `IAsyncDisposable`, which ends the pending readers.
  - `Can(string)`: false before the first read, which it starts; never throws; false where the server maps no capabilities route.
  - `RefreshCapabilitiesAsync` (also run after any 403) and `CapabilitiesChanged`.
  - `SendCommandAsync<TCommand>(command, cancel)` and `SendCommandAsync<TCommand,TResult>`.
- HTTP transports for `/command`, `/command/{id}` and `/capabilities`. The streamed ones are read with `SseParser` and `WebAssemblyEnableStreamingResponse`, as `LiveAsync` does. Answers are handled like this:
  - every response goes through `RecordServerHeaders`;
  - 400/403/stale go through `ResponseFailure.Read` and throw, as queries do;
  - **a 404 with a Scry body on `/command` becomes a `Failed` outcome** carrying the body's message (a lost race, not a bug);
  - a 404/405 without one is `NotSupportedException` "This server is not serving commands", as `NotEnabled` is for live queries.
- Constructor delegates, beside `subscribeTransport`: `commandTransport: Func<CommandRequest, Cancel, IAsyncEnumerable<CommandReceipt>>?`, `receiptTransport: Func<Guid, Cancel, IAsyncEnumerable<CommandReceipt>>?`, `capabilitiesTransport: Func<Cancel, Task<CommandCapabilities>>?`. With none of them, commands are refused with a directed `NotSupportedException` and `Can` answers false. Without `receiptTransport`, a cut stream ends `Unknown`.
- A pending outcome's reader re-attaches on a cut or an `end`, timed by `Reconnect`. A 404 ends it `Unknown`, and a rejection ends it `Failed` with its message.
- Sidecar: `ScrySidecarKind.Command`, and `ScrySidecarHandler.Classify` by path. A JSON answer is recorded whole, and an event-stream answer headers-only, using the response content-type check the handler already makes for `Subscription`. `ScrySidecar.razor.cs` names command entries.
- A command sent over the hub never passes the handler, so the sidecar needs a second source. `ScryClient.CommandActivity` (sent, pending, re-attached, completed, failed, unknown) is raised over every transport, as `LiveActivity` is, and `ScrySidecarStore.Observe(client)` lists commands from it. Entries are keyed by command id, so an HTTP exchange and its reports are one entry. The sample's `LiveTransport` already observes its hub client.
- Tests (`src/Scry.Tests`, a stub `HttpMessageHandler` + a `Pipe`-backed event stream in a new `CommandStub.cs`):
  - `CommandClientTests`: inline; pending past `CommandWait`, then complete; cut, then re-attached; a re-attach 404 → `Unknown`; `end` → re-attach; typed result; 400/403 throw, and a 403 refreshes capabilities; 404 → `Failed`; no route → `NotSupportedException`.
  - `CapabilityTests`; sidecar classification; a command sent through a custom transport, listed from `CommandActivity`.
- Also: narrow the sample's `QueryCacheHandler` key to URLs carrying `QueryUrl.Parameter` (commit 8), so `GET …/command/{id}` and `…/capabilities` never enter it.

### 6. SignalR

- `ScryHub` gains:
  - `Command(string request) : IAsyncEnumerable<string>`: receipts as `ScryJson` strings, with a refusal as the coded error marker, the last item;
  - `Receipt(string id) : IAsyncEnumerable<string>`;
  - `Capabilities() : Task<string>`.

  It is the same processor, so the same limits apply. The caller is `options.Caller` over `Context.GetHttpContext()`.
- `ScrySignalRClient` supplies the three delegates. It cancels a linked token of its own when a stream is left, as it does for `Subscribe`.
- A write over a hub has no `RequireJson`, and a cross-site WebSocket handshake is not subject to CORS. So where a hub authenticates by cookie, the host keeps `SameSite` at `Lax` or `Strict`, or checks `Origin`. `MapScryHub`'s remarks and `docs/security.md` say so.
- Tests: `IntegrationTests/SignalRTests.cs` covers inline, pending, re-attach after the connection drops, and each refusal surfacing as the same exception it is over HTTP.

### 7. Pending-work panel (`src/Scry.Client/Commands/`)

- `ScryCommandStyles.razor`: a `<link>` to `_content/Scry.Client/scry-commands.css`, with an `Href` parameter; null renders nothing.
- `ScryPendingWork.razor(.cs)`:
  - a launcher with a count, and `<aside data-testid="pending-work">`;
  - items with `data-status`/`data-command`;
  - elapsed time re-rendered by a `PeriodicTimer` while anything is pending;
  - an `AutoOpen` parameter, and `Clear finished` / `Close`.
- `wwwroot/scry-commands.css`, in the sidecar's `light-dark()` conventions (launcher bottom-left; the sidecar's is bottom-right).
- No JS module. Every component starts with `@namespace Scry`, and subscribes/unsubscribes with `InvokeAsync(StateHasChanged)`, as `ScrySidecar` does.
- No `ScryActivity`: what another user did arrives as the rows changing.
- Tests (`samples/Sample.Tests`, bUnit, no server; link `CommandStub.cs` like `SnapshotScrubbers`):
  - `PendingWorkComponentTests`: idle renders only the stylesheet; opens when a command goes pending; completes, then drops after the linger; shows the error; shows `Unknown`; close/clear.
  - `ClientRegistrationTests.RegistersTheStoresAtTheClientsLifetime`.
  - `SidecarComponentTests.NamesACommandEntry`.

### 8. Sample end to end (in-process handlers)

Model:
- `samples/Sample.Model/Commands/`: `DeleteEmployee`, `RenameEmployee`, `SetEmployeeActive { Id, Active }`, `CreateEmployee` + `EmployeeCreated` (snippet `commandMessages`). `SetEmployeeActive` is what makes the row-level story repeatable. Bob is the only inactive seed employee, and nothing else in the sample can change `Active`. With it, deactivating a row enables that row's Delete as the live query's next answer, and deleting it removes the row.
- `Messages/RepriceOrder` gains `[Command(typeof(Order))]`, and its remark about the generator never seeing it is rewritten.

Handlers: a new `samples/Sample.CommandHandlers`, a C# class library in `Scry.Samples.slnx` referencing `Scry.Server` and `Sample.Model`. It runs no generator, so it adds no `Scry.Generated` copy. It holds:
- The handlers:
  - `DeleteEmployeeHandler` refuses an employee who manages others with a `ScryCommandException` saying so. That is the sample's one client-visible failure: Alice, once deactivated.
  - `RenameEmployeeHandler`: a name containing "slow" delays `SampleCommandOptions.SlowDelay` (default 5 s, bound from `Sample:Commands`; snippet `slowCommandHandler`).
  - `SetEmployeeActiveHandler`.
  - `CreateEmployeeHandler : ICommandHandler<CreateEmployee, EmployeeCreated>`.
  - `RepriceOrderHandler`: what `/api/orders/{id}/reprice` did.
- The policies:
  - `DeleteEmployeePolicy : ICommandPolicy<DeleteEmployee, Employee>`, with `Rows => _ => !_.Active` (snippet `commandPolicy`).
  - `CreateEmployeePolicy : ICommandPolicy<CreateEmployee>`, whose `Allow` reads `SampleCommandOptions.AllowCreate` (default true), so a test can show the facade bool false. Every other `Allow` in the sample is true, so without it no sample ever shows a type-level denial.
- One registration: `services.AddSampleCommandHandlers(configuration)` and `options.UseSampleCommands()`, which sets `MaxPendingCommands`, `CommandSyncWindow = 1 s` and the policies (snippet `commandRegistration`).

It is a project rather than a linked folder for two reasons. `Sample.FSharp.Tests` is F# and cannot compile a linked C# file. And a host with commands on must route every command, so each host needs all of the handlers or none. It is also the one place the server-side command types are named, so no host that also imports `Scry.Generated` writes an ambiguous name.

Hosts (`MaxPendingCommands` stays zero unless a host says otherwise):
- `Sample.WebServer`: both registration calls. `/api/orders/{id}/reprice` goes; `reprice-bulk` stays, because it is what `changesNotify` documents.
- `ScryTestServer`: `StartAsync(commands: true, slowDelay: …)`, following `liveQueries: true`. It already runs with the poll off and a 50 ms throttle, so a missing change signal fails a test instead of hiding behind the poll.
- F# `ScryServer`: references the project and turns commands on.
- `BackplaneHost`: `Configure` turns commands on, and `Map` drops its own `/api/orders/{id}/reprice`.
  - `Sample.RedisServer` and `Sample.MessagePipeServer` then run `RepriceOrder` in-process, and the docs' two-node recipe writes with the console's `--reprice` on the node not being watched.
  - `Sample.NServiceBusServer` gets the same through `BackplaneHost`, and commit 9 has its adapter claim `RepriceOrder`.

  So one generated command is served in-process by three hosts and by a worker on the fourth.
- IntegrationTests: the existing hosts stay off. A new `CommandRoundTripTests` host turns them on with the shared project and sends every generated command over HTTP: the typed result, the enum payload by name, a slow rename's streamed receipt, and a delete arriving as a live query's next answer. It does for commands what `HttpRoundTripTests` does for queries.

Clients, each showing something the others do not:
- `samples/Sample.WebClient`:
  - `Pages/Commands.razor(.cs)` at `/commands`. The table is a live query (`.Live().Subscribe(...)` over `Employee`, with `Active` and `CanDeleteEmployee`), so every change arrives as its next answer with no refetch code: a delete, a slow rename landing, another user's change, and a Delete button enabling after its row is deactivated.
    - Per row: Deactivate/Reactivate (`SetEmployeeActive`), Rename (bound to `Query.Commands.CanRenameEmployee`), and Delete (bound to `row.CanDeleteEmployee`).
    - The create form is bound to `Query.Commands.CanCreateEmployee` and uses the typed outcome.
    - `data-ready` is set after `Client.Ready` and the first answer, and `[data-testid=command-status]` shows the result.
    - Snippets: `commandsPageMarkup`, `commandsPageQuery`, `commandsPageSend`.
  - `LiveChrome.Reprice` sends `Transport.Query.Commands.RepriceOrder(new() { Id = 1 })`, over SSE or the hub, whichever the toggle selects. The `#reprice` button stays.
  - `App.razor` gets `<ScryPendingWork />` beside `<ScrySidecar />` (snippet `pendingWorkMarkup`). Plus the Index nav link, `app.css`, and the narrowed `QueryCacheHandler` key.
- `Sample.ConsoleClient`: `--reprice <id>` prints the outcome, and awaits `Completion` when it is `Pending`, which makes it the plainest host for the `Pending` path. It pairs with `--live` across two terminals, and with `--server` it drives the backplane and NServiceBus recipes that used `curl` (snippet `consoleCommand`).
- `Sample.WinFormsClient`:
  - The live grid's projection gains `Id`, as a hidden column: a command names its row by key, and keys are not injected.
  - Rename acts on the selected row.
  - A list is bound to `client.PendingWork`, whose `Changed` arrives on the UI thread. This is the one place the store's synchronization-context behaviour is shown (snippet `winFormsCommand`).
- `Sample.WpfClient`: an `ICommand` over `RenameEmployee`, with `CanExecute` from `Query.Commands.CanRenameEmployee` and the selection, re-raised on `CapabilitiesChanged`. That is the XAML shape of binding a capability. The projection gains `Id` (snippet `wpfCommand`).
- F#:
  - `samples/Sample.FSharp/Commands.fs`: a rename (`query.Commands.RenameEmployee(RenameEmployee(Id = id, Name = name))`) and a create read through `ScryCommandOutcome<EmployeeCreated>` (snippet `fsharpCommand`).
  - `Sample.FSharp.Tests/CommandTests.fs`: `RenameCompletesInline`, `CreateAnswersWithTheTypedResult`, `ALiveQuerySeesTheRename` (dispose the client before the server).
  - `LiveTests` renames through the command rather than the `ScryServer.Rename` back door, which goes if nothing else uses it.

Tests:
- `CommandsPageTests`, bUnit over its own `ScryTestServer` (`CommandWait` 300 ms, slow delay 1 s):
  - renders with buttons;
  - only an inactive row's Delete is enabled;
  - deactivating a row enables its Delete as the next live answer;
  - deleting removes the row as the next answer;
  - deleting a manager fails with the handler's message, in the status and the panel;
  - create waits for capabilities, and is disabled where `AllowCreate` is false;
  - a slow rename goes to the panel, then lands;
  - typed result.

  Tests that delete work on rows they created (`CreateEmployee` → `SetEmployeeActive` → `DeleteEmployee`), so they share a fixture database as `LivePagesTests` do and never depend on Bob.
- `CommandUiTests` (Playwright, `[Category("Browser")]`): deactivate, then delete; a slow rename shows in the panel until it lands; a second browser context's rename changes the first page's table; create answers inline.
- `LivePagesTests` and `LiveUiTests` stay unchanged in shape. `LiveUiTests`' SignalR case switches the transport and then clicks `#reprice`, so it now sends a command over the hub end to end.
- `SidecarComponentTests.NamesACommandEntry` and `ListsACommandSentOverTheHub`.
- `UiScreenshotTests.SampleCommands` and `SamplePendingWork`: verify `page.Locator("body")`, because a page holding a live query never reaches `NetworkIdle`. Pin the elapsed text as `SampleSidecar` pins durations. The docs embed the PNG.

Moves:
- `UiSnapshotTests.HomePage`, `UiScreenshotTests.SampleHomePage.*`, and the `IndexPageTests` inline node counts (nav link).
- `UiScreenshotTests.ExplorerSchemaPane.png` and `ExplorerIntelliSense.png` (badges, `CanDeleteEmployee` completion), and `UiSnapshotTests.ExplorerIntrospectionEndpoint`.
- Any whole-`Employee` or whole-`Order` request/response snapshot, since `Employee` gains `CanDeleteEmployee`, `CanRenameEmployee` and `CanSetEmployeeActive`, and `Order` gains `CanRepriceOrder`.
- The `winFormsLive` snippet region, whose projection gains `Id`.

`WireFormatTests.EmployeeQueryWireFormat`, the F# snapshots and `SampleLive` use explicit `Select`s and should stay still.

### 9. NServiceBus: commands in the existing `Scry.Server.NServiceBus`

- Web side:
  - `options.UseNServiceBusCommands(Action<BusCommands>? configure)` → `AddDispatcher<NServiceBusDispatcher>()`, with `BusCommands.For<T>()` / `ForAll()`.
  - By default it claims every command implementing `NServiceBus.ICommand` and nothing else, so referencing the package never takes a command from an in-process handler. The sample's `RepriceOrder` is a command by the endpoint's `DefiningCommandsAs` convention, and is claimed with `For<RepriceOrder>()`.
  - `NServiceBusDispatcher` sends through the host's `IMessageSession`, routed by the endpoint's own routing, with `scry-command-id` (`"D"`) and `scry-caller`.
  - `ScryCommandCompletedHandler` calls the pipeline's `Complete`/`Fail`, so audit #2, `handling.duration` and the target notify fire.
- `ScryCommandCompleted : IMessage` lives in the package, as `ScryChanged` does. Its `Payload` is a `ScryJson` string (succeeded, result, error, at). It has no `TimeToBeReceived`: unlike a change, which the poll would catch anyway, a completion is the only word the node gets.
- The reply has to reach the node holding the stream. That means one endpoint per node, as the backplane already requires (`Sample.Web.{port}`), or `MakeInstanceUniquelyAddressable` where nodes share a name.
- Worker side:
  - `endpointConfiguration.UseScryCommands()` registers a behavior at the same `IIncomingLogicalMessageContext` stage `ScryChangesBehavior` uses.
  - After the handlers return, when the message carried `scry-command-id`, it calls `context.Reply(ScryCommandCompleted)`, with the result set through a `SetScryResult(object)` extension.
  - The reply leaves beside the `ScryChanged` publish, through the message's context. So it leaves only if the handler's work was kept (with the outbox, after its transaction commits), and once for a retried message.
  - A message that exhausts recoverability is answered `Failed` from the error-queue notification, not from the behavior, which sees every attempt.
  - It is independent of `UseScryChanges()`: a worker may reply without publishing changes where the web tier runs `UseDeltaChanges`.
- Sample:
  - `Sample.NServiceBusServer` already has commands on through `BackplaneHost` (commit 8). It claims `RepriceOrder` with `UseNServiceBusCommands(_ => _.For<RepriceOrder>())`, routed to `Sample.Worker`; the adapter's claim beats the in-process handler `BackplaneHost` registered. It drops `/api/orders/{id}/reprice-via-worker`.
  - `Sample.NServiceBusWorker` adds `UseScryCommands()`.
  - Snippets: `sampleNServiceBusCommands` and the updated `sampleNServiceBusWorker`.
  - `NServiceBusSampleTests.AWriteMadeByTheWorkerReachesALiveQueryOnTheServer` stops posting to the endpoint and asserting 202. It sends `Scry.Generated.RepriceOrder` instead (qualified, since the file imports `Sample.Model`) and awaits the outcome's `Completion`. The learning transport's round trip usually outlasts the 1 s window, so this also runs the streamed receipt over a real bus.
  - The docs' worker recipe becomes `Sample.ConsoleClient -- --reprice 1 --server http://localhost:5101`.
- Tests (`src/Scry.Backplane.Tests`, beside `NServiceBusBackplaneTests`, over the learning transport): `SendsThroughTheBusAndCompletesWithinTheWindow`, `CarriesTheHeaders`, `ATypedResultTravelsBack`, `AHandlerThatExhaustsRetriesIsReportedAsFailed`, `ARemoteWorkerCompletesTheCommand`, `TheReplyAndTheChangeLeaveTogether`, `TheAdapterClaimsOnlyWhatItWasTold`, `TwoAdaptersClaimingOneCommandRefuseStartup`.
- The package's `.csproj` description and `nuget-readme.md` cover both halves.

### 10. MassTransit, Rebus, Wolverine (one commit per package, Wolverine last)

Shared shape: `src/Scry.Server.{MassTransit,Rebus,Wolverine}`, the naming the NServiceBus package set for everything Scry does over one bus. Each is net10.0, `RootNamespace Scry`, `ProjectReference ..\Scry.Server`, added to `src/Scry.slnx`, with `PackageVersion`s in `src/Directory.Packages.props` and a comment pinning MassTransit to 8.x.
- `options.Use{Bus}Commands(Action<BusCommands>? configure)` → `AddDispatcher<{Bus}Dispatcher>()`. These claim nothing until told.
- `{Bus}Dispatcher : ICommandDispatcher` sends the bound command with `scry-command-id` and `scry-caller`.
- Web side: a handler/consumer for the package's own completion message, calling `Complete`/`Fail`.
- Worker side, with `SetScryResult(object)` as for NServiceBus:
  - MassTransit: `IFilter<ConsumeContext<T>>` via `UseConsumeFilter(typeof(...<>))` from `AddScryCommands()`, publishing rather than sending so no endpoint convention is needed.
  - Rebus: an `IIncomingStep` after `DispatchIncomingMessageStep`, via `OptionsConfigurer.EnableScryCompletion()`.
  - Wolverine: middleware from `WolverineOptions.UseScryCommands()`, with the result held in a `ConditionalWeakTable<Envelope, object>` because handler return values cascade as messages.
- None of these carries a change backplane this round. A worker on one reaches live queries through the target notify on completion, a change probe or the poll, and `docs/commands.md` says which. Each package is where that bus's `IScryChangeBackplane` would go later.
- Tests go in `src/Scry.Server.Bus.Tests`, apart from the backplane tests so a JasperFx restore problem stays out of them. Per bus: the NServiceBus list minus `TheReplyAndTheChangeLeaveTogether`. Remote workers over MassTransit `UsingInMemory`, a shared Rebus `InMemNetwork`, and Wolverine local queues.
- The API names above come from memory (none is in the local NuGet cache), so verify each against the package. Watch `NuGetAuditMode=all` on each restore. Wolverine's `JasperFx` chain meets `Microsoft.CodeAnalysis.* 5.3.0 Pinned="true"`, and its runtime codegen under that pin is unverified.

### 11. Docs

New `docs/commands.md`, covering:
- commands: what and why;
- authoring (`commandMessages`), including annotating an existing bus message;
- the client surface: `Query.Commands`, `Can*`, and the row bool, which is live inside a live query (`commandsPageMarkup`, `commandsPageSend`);
- handlers (`commandRegistration`, `commandHandlerInterface`) and policies (`commandPolicy`, `commandPolicyInterface`);
- turning them on (`MaxPendingCommands`, `scryOptionsCommands`);
- seeing the result: the page's live query, the interceptor, `Notify` for bulk handlers, and the target notify (`commandsPageQuery`);
- a command that takes longer: the sync window, the streamed receipt, re-attach, `CommandWait`, `Unknown`, the pending panel (`pendingWorkMarkup`, the `SamplePendingWork` PNG), and HTTP/1.1 vs HTTP/2 vs the hub;
- over SignalR;
- bus adapters (`sampleNServiceBusCommands` and one registration snippet each);
- non-HTTP hosting (`ScryProcessor.SendCommand`, the client delegates);
- security notes;
- limits: no cross-command transactions, no ordering across a bus, no client retry or offline queue, re-attach is node-local;
- F# (`fsharpCommand`).

Edits:
- `readme.md`: a write paragraph after "Run time"; the packages table (the NServiceBus row covers commands, plus three new rows); "Then send a command" in At a glance; the docs list.
- `docs/readme.md`: the guide table.
- `docs/comparisons.md:57,130,158,213` (the read-only claims), `docs/linq-coverage.md:208`, and `docs/attachments.md:194` (uploads are a command).
- `docs/live-queries.md`: a "Writes" section (a command's save is reported like any other, and the sample's reprice button is a command); the NServiceBus section gains the command half.
- `docs/security.md`:
  - a new "Commands" layer: server-typed binding, the one-answer 404, re-attach by caller, capabilities as advisory, commands off by default, and the hub note;
  - "What Scry does not do": retry, order, dedupe;
  - checklist rows: commands on only where intended, a policy on every targeted command, and a cookie-authenticated hub keeping `SameSite` or checking `Origin`.
- `docs/wire-format.md`, a Commands section: the request; the receipt with its status table; the streamed receipt in the live-query framing; re-attach; capabilities; the three codes; the hub methods; and a stamp note.
- `docs/server.md`: the routes table (4 → 7), options, and `SendCommand` under non-HTTP hosting.
- `docs/observability.md`: `scry.command`, the two histograms, `scry.server.commands.pending`, and `ScryAuditEntry.Command`.
- `docs/caching.md`: a command's write reaches live queries at once, and conditional GETs when the freshness token moves.
- `docs/sample.md`:
  - the project table gains `Sample.CommandHandlers`;
  - "Running it" gains the console's `--reprice`;
  - a Commands section covers the `/commands` loop (deactivate, delete, a refused delete, a slow rename, create) and the console, desktop and F# angles;
  - the Live controls table says Reprice is a command, carried by whichever transport the toggle selects;
  - the two-node and worker recipes swap `curl` for `--reprice`;
  - "What the traffic looks like" gains a command exchange (request, receipt, a streamed pending receipt);
  - "Integration tests" gains `CommandRoundTripTests`.
- `docs/clients.md`: the three delegates, and each host's command angle.
- `claude.md`: a Commands paragraph beside the live-query one, `Sample.CommandHandlers` in the samples bullet, and the generated-name rule under Cross-cutting rules.
- `docs/explorer.md` (commands in the schema pane), `docs/schema-versioning.md` (commands move the stamp), `docs/sidecar.md` (the command kind, streamed receipts, and hub-sent commands from `CommandActivity`), `docs/annotations.md` (`[Command]`, `[CommandIgnore]`), and `docs/fsharp.md`.

Prose must pass MarkdownSnippets' `ValidateContent`:
- Banned words include you/we/our/your/us/please/just/simply/simple/easy/feel/think.
- Banned phrases include "in order to", "prior to", "a lot", "a number of" and "kind of".
- No `! `.
- New placeholders need `<!-- snippet: x -->` + `<!-- endSnippet -->` on the next line.

Regenerate with `dotnet build src/Scry.Tests/Scry.Tests.csproj`.

## Cross-cutting rules

- Conventions: internal types in the global namespace with no `namespace` line; public types in `namespace Scry`; lambda parameters `_`; comments on their own line above code; the `Cancel` alias; expression-bodied members; no `PackageReference` versions (CPM, with the `PackageVersion` in the tree's `Directory.Packages.props`); `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` in `src`.
- Lockstep readers: `MetadataModelReader` / `Schema` / `ModelSynthesizer` and the two `StampMembers` mirrors (+ the new `StampCommandMembers`) change together. `LockstepTests` is the guard.
- Generated command classes share their names with the server's, as generated enums already do (`Status`). A file that imports both `Sample.Model` and `Scry.Generated` (`ScryTestServer.cs`, `NServiceBusSampleTests.cs`, the F# `ScryServer.fs`) qualifies one side. `Sample.CommandHandlers` keeps the server-side names out of those files.
- One push model: a pending receipt uses the framing, heartbeat clock, lifetime and ticket expiry that live queries use (`EventStream`), and the client reconnects under the same `IScryRetryPolicy`. No second channel, no client ids.
- Non-disclosure:
  - Absent, hidden and row-denied targets answer one 404 after one query.
  - A receipt reaches only the connection that sent the command, or a re-attach by the same caller. With no caller (anonymous), the id is the capability, and it never leaves the client that minted it.
  - Nothing about a command reaches another client except through that client's own live-query answers, run through its own policies.
  - Error messages name wire names only and are bounded.
  - Receipts never ride `IScryChangeBackplane`, which names entities, never rows.
- Build discipline:
  - Never build/test two trees concurrently.
  - Never `dotnet publish` a single-TFM `src` project.
  - Recovery for the usings storm is `dotnet build src/Scry.slnx`; run `dotnet restore src/Scry.slnx` after any stray publish.
  - Stop any running `Sample.WebServer` before building samples.
  - A new sample test server that writes takes a LocalDB suffix of its own, as the backplane hosts, `NServiceBusSampleTests` and the F# tests do.
- Snapshots: inspect every `*.received.*` against its `*.verified.*` before moving it; the lists above say which moves are expected.

## Verification

1. `dotnet build src/Scry.slnx`, then `dotnet test src/Scry.slnx`: generator, wire, schema, capability, pipeline, tracker, client and explorer tests, plus the NServiceBus command tests in `Scry.Backplane.Tests` and `Scry.Server.Bus.Tests` for the other three.
2. `dotnet test IntegrationTests/IntegrationTests.slnx`: `CommandHttpTests` over `TestServer` (inline, pending-then-streamed, re-attach, non-disclosure, capabilities per identity, `RequireAuthorization` on all routes, a live query seeing the write), the `SignalRTests` additions, and `CommandRoundTripTests` (the generated sample commands over HTTP).
3. `dotnet test samples/Scry.Samples.slnx`: bUnit page/component tests, F# `CommandTests` and `LiveTests`, Playwright `CommandUiTests`, `LiveUiTests`' SignalR case, screenshots, and `NServiceBusSampleTests`. Needs LocalDB and Playwright's Chromium. Re-baseline flow from memory: `DiffEngine_Disabled=true dotnet test samples/Scry.Samples.slnx --filter "FullyQualifiedName~UiScreenshotTests"`, view the received PNGs, `mv` them over verified, then run the behavioural suite.
4. Manual, through the Browser pane against `dotnet run --project samples/Sample.WebServer --urls http://localhost:5249`:
   - `/commands` shows Delete enabled only on Bob, the inactive employee.
   - Deactivating Carol enables her Delete with no reload. Deleting her removes the row.
   - Deactivating and deleting Alice fails with the handler's message, because she manages others.
   - Renaming to "Carol slow" opens the pending panel, and the row changes when it lands.
   - A second tab's rename changes the first tab's table.
   - On the live pages, Reprice updates the orders table over SSE, and again after the toggle over SignalR.
   - The sidecar lists command exchanges, a pending one as a stream, and a hub-sent one from its activity.
   - The explorer's schema pane lists the commands, with the `command` badge on `CanDeleteEmployee`.

   Then the other clients against the same server:
   - `Sample.ConsoleClient -- --reprice 1`, while `-- --live` reprints in a second terminal.
   - The WinForms live grid drops an employee deactivated on the web page, and its pending list shows a slow rename.
   - The WPF Rename button follows the capability and the selection.

   Then `Sample.NServiceBusServer` + `Sample.NServiceBusWorker`, with `--reprice 1 --server http://localhost:5101`: the command completes through the worker, and the watching console reprints. Where a Redis is at hand, the same on two `Sample.RedisServer` nodes, writing on the node not being watched.
5. `dotnet build src/Scry.Tests/Scry.Tests.csproj` regenerates every snippet region. Confirm there is no `ValidateContent` failure and no stale anchor.

## Flags carried into implementation

- `SignatureDecoder.IsSystemType` returning false (`SignatureDecoder.cs:118`) is a real prerequisite bug for `typeof(...)` decoding; fix it first.
- `;` inside `build_property.ScryCommandDll` through the generated editorconfig is unverified. The generator splits on `;` and `|`; switch the MSBuild join to `|` if the value is truncated.
- Settled or moot since the first draft:
  - `ServerSentEventsResult`'s `Cache-Control`: moot. SSE is written by hand (`WriteEvent`), since the framework's formatter never flushes, and commands share it.
  - `HttpClient.Timeout` over a streamed body is now a shared assumption. Live queries hold streams for up to `SubscriptionLifetime` through the same `HttpClient`, so it is one check for both.
  - Composite-key watch translation: moot.
- HTTP/1.1: a pending command holds a stream, as a live query does, and a plain `http://` dev server shares six connections per origin between them. HTTP/2 or the hub avoids this.
- `Notify(target)` on completion beside the interceptor's report is expected to conflate in the lease's due flag; a test pins it at most one extra run.
- NServiceBus: confirm against 10.2.9 how the reply is routed to the node holding the stream (`MakeInstanceUniquelyAddressable` where names are shared), and the error-queue hook for a final failure.
- The remaining buses' APIs are from memory. The likeliest surprises are MassTransit in-memory transport sharing across two bus instances, replying from a Rebus `IIncomingStep`, and Wolverine's `DeliveryOptions.Headers` and middleware signatures. Wolverine goes last.
- A `Can*` row member joins every whole-model query's default projection, and a live query pays for it on every run. Explicit `Select`s avoid that.
- `SubscriptionCaller` → `Caller` renames a just-merged public option (beta). If that is unwanted, commands read `SubscriptionCaller` and the name stays.
- Line references were re-derived after the live-query merge and will drift again.

## Out of scope

- **Other users' in-flight activity**: the first draft's `/watch`, `.Watched()`, `activity` events and `ScryActivity`. Completed work already arrives through live queries. If it comes back, the design to reach for rides the live query rather than a second channel. The server would send an `activity` event on `/subscribe` to the live queries whose current answer holds the target row. That is non-disclosing by construction, since the answer was already run through the caller's policies, and additive, since a live-query client skips event types it does not know. The cost is key extraction per run and a key set per lease.
- A multi-node tracker. Re-attach is node-local, and receipts must not ride `IScryChangeBackplane`.
- Backplane halves for MassTransit, Rebus and Wolverine.
- Cached (`ICachedRowPolicy`-style) command policies; running commands from the explorer; client retry or offline queue; cross-command transactions.
