# [Attachment] claim-check plan

## Context

Scry's only binary story today is `[BinaryTransfer]`: the query still selects the `byte[]`, and the bytes
travel as multipart parts rather than base64. There is no way to expose a binary column without paying its
transfer on every row. `[Attachment]` adds the claim-check pattern: the member is **never fetched by the
query at all**; the generated client member becomes a lazy handle whose invocation makes a second HTTP call,
authorized per-row/per-member by a mandatory security check, answered as a raw stream. The lookup is derived
entirely from the client query structure (row primary-key values + member name); a query shape from which it
cannot be derived is a **build error** (analyzer) with a translator exception backstop.

## Decisions

| Decision | Choice | Noted alternative |
| --- | --- | --- |
| Client member type | `ScryAttachment` sealed class (Scry.Client, ns `Scry`), `Task<Stream?> OpenAsync(Cancel cancel = default)`; null = DB NULL | raw `Func<Cancel, Task<Stream>>` — isolated to generator emit + materializer, cheap to swap |
| Security check | **Mandatory**: startup throws if an `[Attachment]`-bearing source has no `IAttachmentPolicy<T>` (attribute `[AttachmentWith(type)]` or `options.AddAttachmentPolicy<TEntity,TPolicy>()`) | optional |
| Deny semantics | 404 for denied / missing / row-policy-filtered alike (no existence oracle); 204 for DB NULL; 400 wire/validation; 500 fixed message | 403/404 split |
| Scope | Full round: core + introspection + explorer + sample model + docs | core only |
| Applicability | `byte[]` scalars on Entity-kind sources only; refused on views/pocos/complex, non-byte[], or combined with `[BinaryTransfer]` — generator build error AND server startup throw | — |
| Row policies | Always additionally applied: fetch resolves the row through the policy-filtered source | — |
| Query wire | **Unchanged**, no version bump. Attachment members not wire-addressable (validator rejects → 400); client never emits them | — |

## Cross-cutting design (each used by several steps)

- **A1 PK metadata**: convention keys are computable without an `IModel`, so `Schema.Build` computes and
  stores them (`TypeMeta.AttachmentKeys`, new prop on [TypeMeta.cs](src/Scry.Server/TypeMeta.cs)) for
  attachment-bearing types only. `Schema.ValidateAgainstModel` ([Schema.cs](src/Scry.Server/Schema.cs)
  ~:510), called from `MapScry` where a live model exists, then verifies convention keys == real EF PK
  (name-set equality) for every attachment-bearing source present in the model; mismatch → throw naming
  `[Key]` as the fix. Model-absent types are skipped (existing philosophy; keeps fixtures containable).
- **A2 Canonical key order**: ordinal sort by member name, both sides. Wire keys are ordered and nameless;
  EF's PK order is invisible to the generator so it cannot be canonical. Documented on `AttachmentRequest`.
- **A3 Convention key derivation** (lockstep pair, like the classifiers): over allow-listed scalar members
  incl. inherited — all `[System.ComponentModel.DataAnnotations.Key]` members; else `Id`; else
  `{TypeName}Id`. Every key must itself be an allow-listed scalar (PlanSeek precedent,
  [QueryExecutor.cs](src/Scry.Server/QueryExecutor.cs) ~:931). Underivable → generator error / startup throw.
- **A4 Schema shape**: new `MemberKind.Attachment` ([MemberKind.cs](src/Scry.Server/MemberKind.cs)) —
  automatically excluded from default projection, PlanSeek, enum walk, and `ResolvePath` scalar leaves; plus
  an explicit early rejection in `QueryValidator.ResolvePath` (~:1383) with a pointed message. Grep
  `MemberKind\.` / `\.Kind ==` across Scry.Server and audit every switch.
- **A5 Generated metadata**: `ScryModelAttribute` gains optional named props (back-compatible; ctor
  unchanged): `public string[] Keys { get; set; }` and `public string[] Attachments { get; set; }`, emitted
  only for attachment-bearing models. Read by analyzer, translator, materializer; `ModelSynthesizer` emits
  identically.
- **A6 Startup-guard testability**: shape guards + key derivability + policy presence throw in
  `Schema.Build`; PK verification throws in `ValidateAgainstModel`. Because schema discovery is an assembly
  scan (a `[Queryable]` fixture poisons every context in its assembly), there is **no in-assembly negative
  fixture for the missing-policy throw**; it follows the untested `EnsureNoBinaryTransfer` precedent, and
  the equivalent conditions are covered by generator diagnostic snapshots. The PK-mismatch case IS testable:
  the fixture entity carries `[AttachmentWith]` + a convention-visible `Id` (so every `Schema.Build`
  passes) while its own dedicated context fluent-configures `HasKey(_ => _.Code)` — only
  `ValidateAgainstModel` on that context throws.
- **A7 Plan threading**: `QueryTranslator` gains `Translate(Expression, out IReadOnlyList<AttachmentBinding>)`
  (existing overload delegates). `ToScryRequest` keeps its public shape; an internal overload outs an
  `AttachmentPlan`, which `Send`/`Single`/`Page`/`ToListAsync`/`ToAsyncEnumerable` pass with
  `provider.Client` into materialization. Whole-model queries (no `SelectOp`) build the plan from
  `typeof(T)`'s `[ScryModel]` instead.
- **A8 Schema stamp**: for attachment-bearing types only, both stamp emitters append a synthetic member
  tuple `("~keys", "<canonical key names space-joined>")` — `~` can't start an identifier so no collision;
  attachment-free surfaces hash byte-identically (no stamp churn). The member's own TypeDisplay changing to
  `global::Scry.ScryAttachment` already stamps the bearing type (correct: `[Attachment]` IS a surface
  change, unlike stamp-neutral `[BinaryTransfer]`).
- **A9 Client transport**: only `ScryClient.ForHttp` wires the attachment transport
  (`Func<AttachmentRequest, Cancel, Task<Stream?>>`); custom-transport ctor untouched; `OpenAsync` without
  one throws `NotSupportedException` (mirrors `Batch()`/`StreamAsync` refusals).

## Derivability rule (analyzer + translator backstop)

- Whole-model query (no `Select`): derivable — keys are generated members.
- `Select` projection with an attachment leaf at path P (navigations allowed, `_.Manager.Photo`): every key
  member of P's declaring row must be projected as sibling leaves with prefix P (`_.Manager.Id`). Missing →
  error naming the key.
- Conservative refusal whenever attachments would ride under `GroupBy`/`Distinct`/set ops/`Join`/`SelectMany`
  — including whole-model `.Distinct()` and SelectMany elements whose model carries attachments (dedup over
  unique keys is meaningless anyway; relaxable later, one predicate in each of analyzer + translator).
- Attachment members anywhere else (`Where`/`OrderBy`/keys/aggregates/comparisons): error — not values.

## Steps (one commit each unless noted; every step leaves all trees green)

**Step 1 — Wire + annotations.** New `src/Scry.Annotations/AttachmentAttribute.cs`
(mirror [BinaryTransferAttribute.cs](src/Scry.Annotations/BinaryTransferAttribute.cs), `Property|Field`,
snippet region) and `AttachmentWithAttribute(Type policy)` (mirror ReturnableWith). New
`src/Scry.Wire/AttachmentRequest.cs`:
`record AttachmentRequest(int Version, string Root, string Member, IReadOnlyList<AttachmentKey> Keys)` with
`const int CurrentVersion = 1`, `string? Stamp { get; init; }`; `record AttachmentKey(string? Value,
ClrTypeTag Tag)` mirroring ConstNode (server parses into the PK member's type, never trusts the tag). Add
`AttachmentRequest` to `WireJsonContext` (WireMetadataTests sweeps all public wire records — forgetting is a
test failure); `ScryJson` gains Serialize/DeserializeAttachmentRequest via the fail-closed helper.
Tests: wire round-trip snapshot.

**Step 2 — `ScryAttachment` + client transport.** New `src/Scry.Client/ScryAttachment.cs` (internal ctor:
client, root, member, keys; `OpenAsync` posts `AttachmentRequest` with the client's stamp).
[ScryClient.cs](src/Scry.Client/ScryClient.cs): HTTP ctor wires `{endpoint}/attachment` transport;
`PostAttachmentAsync` uses `ResponseHeadersRead`, records the stamp header, maps 200 → stream wrapped so
disposing it disposes the `HttpResponseMessage`, 204 → null, non-success → `ScryRequestException` with
stale-client mapping exactly as `PostAsync`. Extract `ConstantOf`'s value→(Value, Tag) mapping
([QueryTranslator.cs](src/Scry.Client/QueryTranslator.cs) ~:1853) into internal `ValueTag.Of` so the
materializer tags keys identically. Ships inert this commit.

**Step 3 + Step 4 — generator ↔ server lockstep pair (land adjacently; fold into one commit if preferred).**

*Step 3 — generator.* [MetadataModelReader.cs](src/Scry.SourceGenerator/MetadataModelReader.cs): new
attribute consts (`Scry.AttachmentAttribute`, `Scry.BinaryTransferAttribute`,
`System.ComponentModel.DataAnnotations.KeyAttribute`); capture `IsAttachment`/`HasBinaryTransfer`/`IsKey`
per property. `ModelExtract.PropertyInfo` gains those flags; `SourceInfo` gains `EquatableArray<string>
Keys` (populated only for attachment-bearing types → unrelated extracts value-identical, incremental regen
unaffected). [ScryGenerator.cs](src/Scry.SourceGenerator/ScryGenerator.cs): build-blocking diagnostics
SCRY004 (not byte[]), SCRY005 (not an entity source), SCRY006 (combined with BinaryTransfer), SCRY007 (keys
underivable — names the convention, directs to `[Key]`), nothing emitted when any fires; emit
`public global::Scry.ScryAttachment {Name} { get; init; } = null!;` via a shared display helper also used by
the stamp; exclude attachments from `ScalarMembers`; emit `Keys = …, Attachments = …` named args; stamp per
A8. `AnalyzerReleases.Unshipped.md` rows (build fails without). Tests: generator snapshots (attachment
member, `[ScryModel]` args, member absent from entry point; key conventions incl. `[Key]` composite ordinal
order, `{TypeName}Id`, inherited); one diagnostic snapshot each; stamp test (BinaryTransfer stamp-neutral vs
Attachment stamp-changing on the same member).

*Step 4 — server schema.* `MemberKind.Attachment` + audit of every `MemberKind` switch (A4).
[Schema.cs](src/Scry.Server/Schema.cs): `BuildTypeMeta` guards (byte[]-only, no `[BinaryTransfer]` combo,
exposed-scalar-shaped only — mirror `EnsureNoBinaryTransfer` phrasing) and classification; new pass for
attachment-bearing types: entity-kind check, convention keys (A3) → `TypeMeta.AttachmentKeys`, policy
resolution (options-registration first, then `[AttachmentWith]` walking the base chain so a subclass cannot
shed it; single effective policy, nearest wins; verify it implements `IAttachmentPolicy<T>` for a compatible
T; none → throw). New public `IAttachmentPolicy<T>` (single method `bool Authorize(ScryAttachmentContext
context)`, snippet region) + `ScryAttachmentContext` (Services, Db, RequestHeaders documented untrusted,
ResponseHeaders, `string Member`, `IReadOnlyList<object> KeyValues` — parsed, typed, canonical order) +
internal `AttachmentPolicy` mirror of `RowPolicy` (cached reflected invoke).
`ScryOptions.AddAttachmentPolicy<TEntity, TPolicy>()`. `QueryValidator.ResolvePath` explicit rejection.
Introspection: `ScryMemberInfo.IsAttachment` (default-suppressed → zero churn for attachment-free JSON),
`ScryTypeInfo.Keys` (null unless attachment-bearing). Stamp per A8. `ValidateAgainstModel` PK verification
(A1). Tests: new `Contract` entity in [TestModel.cs](src/Scry.Tests/TestModel.cs) (`[AttachmentWith]`, `Id`
key, nullable `[Attachment] Document`, seeded bytes + NULL rows — keeps all existing `ScryProcessor.Create`
call sites untouched); SecurityTests raw-wire rejections (attachment in Where/OrderBy/Select/nested paths →
400); accept `IntrospectionTests.Describe` churn.

**Step 5 — server fetch + endpoint.** `QueryExecutor.FetchAttachment(AttachmentRequest, DbContext,
CallScope)`: version gate → resolve source (unknown → 400) → member via `TryGetMember` (honors
`[PreviousNames]`; not attachment-kind → 400) → key count match (else 400), null key value → not-found (PKs
are never null), parse each via the existing constant-parse path (failure → 400, the "constants that fail to
parse" precedent) → **security check before touching the DB** (DI-first, Activator fallback, cached invoke;
deny → not-found) → row through `ResolveSource` (row policies) → `Where` of parameterized key equality
(`Parameterization.Parameterize` — never `Expression.Constant`, plan-cache flooding) → project the blob
alone as the existing one-slot `object[]` shape (distinguishes no-row from NULL-blob) → single-or-default.
Public `ScryAttachmentResult { bool Found; byte[]? Value; }` + `ScryProcessor.FetchAttachment` overloads
(choke point; non-HTTP hosts get the same path). Audit: `ScryAuditEntry` gains `AttachmentRequest?
Attachment` discriminator; positional `Request` is a synthesized empty-pipeline `QueryRequest` (nullable
`Request` would be a public API break — accepted compromise); activity `"scry.attachment {root}"`,
result-kind tag. Endpoint: fourth entry **inside** the `Endpoints` array
([ScryServiceExtensions.cs](src/Scry.Server/ScryServiceExtensions.cs) ~:36) so auth conventions fan out —
`MapPost($"{pattern.TrimEnd('/')}/attachment", HandleAttachment)`: stamp header first; wire failure → 400 +
`QueryRecorder.Malformed`; `!Found` → bare 404; `Value is null` → 204; else 200 octet-stream with advisory
Content-Length; `ScryValidationException` → 400 (+ drifted-stamp attribution); else 500 fixed
`"Attachment fetch failed."`. Tests: processor-level found/NULL/denied/absent/policy-filtered/wrong
member/wrong arity/unparseable key/previous-name; ObservabilityTests audit snapshot.

**Step 6 — analyzer.** New ERROR-severity rules (the existing `Rule` factory hardcodes Warning — add an
error factory): SCRY113 "attachment requires the row's keys projected beside it" (names the missing key
path), SCRY114 "attachment member is not a value" (predicates/ordering/keys/aggregates/bare value), SCRY115
"attachment cannot be carried under this operator" (GroupBy/Distinct/set-ops/Join/SelectMany, conservative).
`KnownTypes` learns `Scry.ScryAttachment` + reading `[ScryModel]` `Keys`/`Attachments` off model symbols;
detection is structural (property type == `ScryAttachment`), so hand-written models are covered. Walk logic
in `ScryLinqAnalyzer`/`ExpressionRules`; analyzer stays partial/chain-as-written — the translator is
authoritative. `AnalyzerReleases.Unshipped.md`. Tests: AnalyzerTests stub gains the attribute props + a
`ScryAttachment` stub + attachment-bearing model; snapshots for each rule + clean valid cases (whole-model,
keys-projected, navigation attachment).

**Step 7 — client translator + materialization.** Internal `AttachmentPlan`/`AttachmentBinding` records
(target path, root, member, canonical key members, key source paths). Translator: rooted `MemberExpression`
of type `ScryAttachment` in projections → resolve declaring model's `[ScryModel]` (missing keys →
`NotSupportedException` for hand-built models), record binding, **omit from wire projection**; enforce
sibling-key presence, refuse per the derivability rule everywhere else (check before the generic
rooted-member case at [QueryTranslator.cs](src/Scry.Client/QueryTranslator.cs) ~:925); messages match the
analyzer's. Materialization (`AttachmentBinder`, cached per type+plan): whole-model — normal STJ
deserialize, then bind `ScryAttachment` onto init-only props via cached reflection (init setters are
reflection-assignable; WASM interpreter fine — the payload path already reflects over these types; full-AOT
trimming may need `[DynamicallyAccessedMembers]`, noted in a doc comment with a generator-emitted binder as
the escape hatch); projection — re-materialize from `JsonElement` inside the same
`EnumAliasScope`/`BinaryPartScope` wrapping `DeserializePayload` uses (internal `ScryJson` seam, not
duplicated scope handling) so `[BinaryTransfer]` `$bin` placeholders still resolve in the same row;
single-public-ctor types (anonymous, positional records) constructed with ordered args, otherwise init-prop
assignment; property keys are **camel-cased** (`JsonNamingPolicy.CamelCase.ConvertName`); keys tagged via
`ValueTag.Of`. `ScryQueryableExtensions` threads plan + client per A7 (list/single/page/stream/batch).
Tests: wire snapshot proving the attachment member is absent from the projection; binding assertions over
processor-backed transport (full HTTP OpenAsync lands in Step 9); every refusal; mixed BinaryTransfer +
attachment row.

**Step 8 — explorer.** [ModelSynthesizer.cs](src/Scry.Explorer.Core/ModelSynthesizer.cs): `ScalarMembers`
excludes `IsAttachment`; `Arguments` appends `Keys`/`Attachments` — both lockstep with the generator; member
emission needs nothing (TypeDisplay already carries the type). Verify the completion-only workspace resolves
`Scry.ScryAttachment` (RoslynWorkspace always receives Scry.Client references per its doc; if a shape-only
path lacks it, synthesize a one-line stub). Tests: synthesized-source snapshot; generator/synthesizer parity.

**Step 9 — IntegrationTests.** New `AttachmentTests.cs` modeled on
[BinaryTransferTests.cs](IntegrationTests/BinaryTransferTests.cs) (self-contained context/model/server,
incl. the `using static` trick): entity with nullable attachment + self-navigation, `[AttachmentWith]`
policy that denies via a request header (a per-call toggle), `[ReturnableWith]` row policy hiding one
row; hand-written client model with full `[ScryModel(... Keys, Attachments ...)]`. Coverage: whole-model
round trip streaming bytes through `OpenAsync` (read after enclosing scopes close — pins response lifetime);
projection round trip incl. `_.Manager.Photo` + `_.Manager.Id`; 404 denied/missing/policy-filtered; 204
NULL; raw hand-built requests → 400 (wrong member, wrong arity, unparseable key); stamp header on
200/204/404/400; auth fan-out — second server with `.RequireAuthorization()` on the `MapScry` return,
`/attachment` 401s. Plus the contained PK-mismatch fixture (A6): dedicated context, `ValidateAgainstModel`
throws naming `[Key]`.

**Step 10 — docs.** *(Implemented. The sample-model half below was deliberately not done — see
"Deviations" at the end.)* `Employee.Photo` (`[Attachment] byte[]?`, snippet region) + seed;
`EmployeePhotoPolicy : IAttachmentPolicy<Employee>` in **Sample.Server** (Sample.Model references only
Scry.Annotations) registered in the `serverRegistration` snippet; same registration in every other
SampleContext host (`Sample.Tests` servers, `IntegrationTests/HttpRoundTripTests.cs`). Accept churn via
received→verified moves only: regenerated Sample.Client models, WireFormatTests/introspection/stamp verified
files, **UiScreenshotTests PNG baselines**. Docs (edit snippet sources, rebuild Scry.Tests — never the .md
output): `docs/annotations.md` new `[Attachment]` section contrasting BinaryTransfer (encoding vs claim
check; stamp-neutral vs surface change); new `docs/attachments.md` (handle, derivability with examples, key
conventions/`[Key]`, security model, startup guards); `docs/security.md` (endpoint in the layers; What Scry
does not do: no range requests/caching; 404 rationale); `docs/wire-format.md` "Attachment retrieval" sibling
section (endpoint, request snippet, status matrix, stamp header); `docs/policies.md` `IAttachmentPolicy`
beside row policies; readme feature list.

## Known risks / accepted compromises

- **Composite-key wire order** is canonical-by-name, not EF order (invisible to the generator).
  Self-describing named keys would be a wire change later; documented on `AttachmentRequest`.
- **Whole-model `.Distinct()` / SelectMany-element attachments refused** (conservative). Relaxing later is
  one predicate in analyzer + translator.
- **Audit entry** carries a synthesized empty-pipeline `QueryRequest` + `Attachment` discriminator — mildly
  dishonest but avoids a public API break on the positional `Request`.
- **Missing-policy startup throw has no server-side negative test** (assembly-scan poison rule); generator
  snapshots cover the sibling conditions, `EnsureNoBinaryTransfer` precedent.
- **Model-absent attachment types** are only caught at fetch time (500), consistent with
  `ValidateAgainstModel`'s existing skip philosophy; noted in docs.
- **`ScryAttachment` under consumer STJ serialization** emits `{}`; optional write-only converter later.
  Deserialization fails closed (internal ctor).

## Deviations from the plan as executed

- **The sample model was left alone.** Step 10 called for an `[Attachment]` on `Sample.Model.Employee`.
  The docs snippets it was wanted for come instead from the `Contract` fixture in `Scry.Tests`, which is
  real, compiling, and covered by tests — so the sample member would have bought only explorer demo
  visibility, in exchange for regenerating the `UiScreenshotTests` PNG baselines and adding the policy
  registration to every `SampleContext` host (`Sample.Server`, two `Sample.Tests` servers,
  `HttpRoundTripTests`). The `ModelSynthesizer` lockstep it would exercise is implemented and unit
  tested regardless. A follow-up can add it when the demo is what matters.
- **The audit entry was not faked.** The plan accepted synthesizing an empty-pipeline `QueryRequest`
  for an attachment fetch (risk R3). Since the package is pre-release, `ScryAuditEntry.Request` was made
  nullable and `Attachment` added beside it instead — exactly one of the two is ever set, which is
  honest rather than mildly dishonest, at the cost of a nullable-reference change auditors must handle.
- **`EquatableArray<T>` now reads a default instance as empty.** Needed because `SourceInfo.Keys` is an
  optional array: an unset record field is `default`, and an uninitialized `ImmutableArray<T>` throws on
  every member. Caught by the generator snapshots failing loudly rather than subtly.

## Verification

Per step: `dotnet build src/Scry.slnx` → `dotnet test src/Scry.slnx`. Steps 9–10 additionally
`dotnet test samples/Scry.Samples.slnx` → `dotnet test IntegrationTests/IntegrationTests.slnx`, strictly
sequential (shared obj — never concurrent). Verify churn accepted only via received→verified moves after
inspecting diffs. LocalDB required. End-to-end proof: Step 9's whole-model round trip (bytes stream through
`OpenAsync` against the real HTTP endpoint) and the generator↔server stamp agreement in HttpRoundTripTests.
