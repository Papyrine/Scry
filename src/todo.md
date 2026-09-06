# Security review — findings and test backlog

Scope: the hostile-client surface described in `docs/security.md` — wire deserialization, the validator, the schema
allow-list, expression rebinding, row/attachment policies, cursors, the HTTP endpoints (query, stream, batch, attachment,
GET form), the explorer host, and the parts of the explorer UI that handle untrusted input. The source generator (build-time,
trusted input) and the client's handling of a hostile *server* are out of scope, as the doc says they are.

Method: every server file under `src/Scry.Server`, `src/Scry.Wire`, `src/Scry.Annotations`, `src/Scry.Server.Explorer`,
`src/Scry.Server.Delta`, and `src/Shared` was read in full, and the scenarios below were then checked against the existing
tests (`src/Scry.Tests`, `IntegrationTests`, `src/Scry.Explorer.Tests`, `samples/Sample.Tests`). Every finding marked
**confirmed** was reproduced with a scratch NUnit project (a small TPH model over EfLocalDb, plus a `TestServer` host running
`MapScry`) written outside the repo; the repro is not committed. Findings marked **by inspection** were not executed.

Legend for the checklists: `[ ]` a test to add, `[x]` a scenario an existing test already pins (the test is named).

Severity is relative to the threat model: HIGH breaks a documented guarantee against a hostile client; MEDIUM is a real
weakening that needs a deployment or model shape to matter; LOW is hardening or containment; INFO is documentation.


## Findings


### F1 — HIGH — `SelectMany` followed by `OfType` skips the narrowed type's own row policies (confirmed)

`QueryExecutor.Walk` tracks how much of the policy chain the query already carries in `appliedPolicies`
([QueryExecutor.cs:350](Scry.Server/QueryExecutor.cs#L350)). `OfType` applies only the levels of the derived chain past
that count ([QueryExecutor.cs:449-451](Scry.Server/QueryExecutor.cs#L449-L451)). `SelectMany` replaces the row with the
collection's element ([QueryExecutor.cs:455-467](Scry.Server/QueryExecutor.cs#L455-L467)) but leaves `appliedPolicies`
at the **root's** count. The validator allows `OfType` after `SelectMany`
([QueryValidator.cs:86-104](Scry.Server/QueryValidator.cs#L86-L104) has no `sawSelectMany` guard).

So with a root carrying N policies, a `[QueryableCollection]` of an unpolicied element type, and an opted-in derived
element type carrying its own policy, `Root.SelectMany(Collection).OfType(Derived)` applies `Derived.Policies.Skip(N)`.
When N is at least the derived chain's length, **no policy of the derived type runs**, and the derived type's own members
are readable on the rows its policy exists to hide. The element type itself carries no policy, so the startup refusal for
policied collections never fires. `docs/policies.md` §"Inheritance runs downwards only" promises that narrowing applies the
derived policy exactly as rooting at the derived source does; this is the one path where it does not.

Repro (scratch model: `Dept` with a policy and `[QueryableCollection] People`; `Exec : Person` with a policy hiding
sealed execs): `Dept.SelectMany(People).OfType(Exec).Select(Secret)` returned the sealed exec's secret.
`Person.OfType(Exec)` and `Exec` directly both hid it.

**Fixed:** `QueryExecutor.Walk` now resets `appliedPolicies` at the flatten to the element's own chain length (the
whole chain where the element is policied and read through `CorrelateMany`, zero otherwise), so a narrowing after it
skips exactly what the flatten applied. Pinned by `FlattenNarrowPolicyTests` over the new `Fleet` / `Machine` / `Press`
shape in `TestModel`.

- [x] covered: `FlattenNarrowPolicyTests.NarrowingAfterAFlattenAppliesTheDerivedPolicy` (root policied, element not,
  derived policied — the whole derived chain was skipped)
- [x] covered: `FlattenNarrowPolicyTests.NarrowingAfterAFlattenHidesTheDerivedMembersOfADeniedRow`
- [x] covered: `FlattenNarrowPolicyTests.NarrowingAfterAFlattenOfAPoliciedElementAppliesBothChains` (`Hide` element)
- [x] covered: `FlattenNarrowPolicyTests.NarrowingAfterAFlattenOfAnUnpoliciedRootAppliesTheDerivedPolicy`,
  `TheRootPolicyStillFiltersWhatIsFlattened`
- [ ] add: the root carrying **two** policies (a policied base and derived root) over the same shape
- [ ] add: `DeniedRowMode.Error` on the derived policy still fails the request after a flatten (`ProbeSteps.Stop()` is
  called at the flatten, so no probe is planned today — decide whether that is acceptable and pin it)
- [ ] add: `OfType` applied twice down a chain (`Base.OfType(Mid).OfType(Leaf)`) applies each level exactly once
- [x] covered: `PolicyInheritanceTests.NarrowingToASubclassMatchesQueryingItDirectly` (no flatten)
- [x] covered: `PoliciedCollectionTests.FlatteningTheCollectionReachesOnlyTheAllowedElements` (no narrowing)


### F2 — MEDIUM — the attachment endpoint is reachable as a top-level browser navigation and serves scriptable types inline (confirmed)

`docs/attachments.md:143` and `AttachmentAttribute.cs:31` claim "the endpoint answers only POST — there is no way to
navigate a browser to one, so a type that would be scriptable as a top-level document cannot become one here". An HTML
form navigates with POST. With `enctype="text/plain"` and one field whose name is
`{"version":1,"root":"Dept","member":"Page","keys":[{"value":"1","tag":"Int32"}],"pad":"` and value `"}`, the body is
`{...,"pad":"="}` — valid JSON, and the unknown `pad` member is skipped by the deserializer. The endpoint reads any body
regardless of `Content-Type` ([ScryServiceExtensions.cs:356](Scry.Server/ScryServiceExtensions.cs#L356)) and answers
with the bytes inline: declared content type, `nosniff`, no `Content-Disposition`
([ScryServiceExtensions.cs:601-604](Scry.Server/ScryServiceExtensions.cs#L601-L604)).

Repro: a `text/plain` POST of that body to `/api/query/attachment` answered `200 text/html` with the stored
`<script>` document and no disposition header. In a browser that sends first-party cookies on a cross-site top-level POST
(Safari; Chrome's two-minute Lax+POST window; any cookie set `SameSite=None`), attacker-uploaded attachment content runs
on the API's origin as the victim. `image/svg+xml` and `text/html` are both types `AttachmentMedia` knows.

Fix: send `Content-Disposition: attachment` (with a filename from `AttachmentMedia.Extension`) on every attachment
response; add `Content-Security-Policy: sandbox` as belt and braces; enforce `Content-Type: application/json` on every
POST (F3); correct the two doc claims.

**Fixed:** every attachment `200` now carries `Content-Disposition: attachment; filename="{Member}{ext}"` and `Content-Security-Policy: sandbox`, and the endpoint accepts only `application/json` bodies (F3). Pinned by `AttachmentTests.IsServedAsADownload` and `HttpRoundTripTests.ABodyThatIsNotJsonIsRefused`; both doc claims corrected.

- [x] covered (IntegrationTests): an attachment `200` carries `Content-Disposition: attachment`
- [x] covered (IntegrationTests): a `text/plain` form-shaped POST to the attachment endpoint is refused with 415
- [x] decided allowed, documented in `docs/attachments.md` (the download headers are what make them safe): declaring `text/html` / `image/svg+xml` as an attachment content type
- [x] covered: `AttachmentTests.DeclaredContentTypeIsServed` asserts `nosniff`
- [x] docs: fix `docs/attachments.md:143` and the `AttachmentAttribute` remarks


### F3 — MEDIUM — POST bodies are read whatever their `Content-Type` (confirmed)

None of the four POST handlers check `Content-Type`. Every endpoint therefore accepts the `text/plain` form encoding an
HTML `<form>` can produce, which is the classic cross-site vector for a JSON API: a victim's browser executes an
attacker-chosen query with the victim's cookies. The response is unreadable cross-origin, so for the query endpoints the
impact is the side effects — audit entries, `DeniedRowMode.Error` probes, cached-policy scope warming, policy header
writes — and for the attachment endpoint it is F2. `docs/security.md` defers CSRF to the host, but the host cannot fix
this one cheaply: an anti-forgery token has no place in the wire, and requiring `application/json` is the standard
mitigation for a JSON API because a form cannot send it.

Repro: `POST /api/query` with `Content-Type: text/plain` and the form-shaped body answered `200` with the count.

Fix: in each handler, refuse a body whose media type is not `application/json` (415, `no-store`, before reading the body).
The `q=` GET form is unaffected — it carries no body and is already cache-safe.

**Fixed:** `ScryServiceExtensions.RequireJson` refuses any POST whose media type is not `application/json` with a 415 (`no-store`), before the body is read, on the query, stream, batch, and attachment endpoints and the explorer's SQL preview. Pinned by `HttpRoundTripTests.ABodyThatIsNotJsonIsRefused`, `AFormBodyIsRefused`, `ABodyWithNoContentTypeIsRefused`, `AJsonBodyIsAccepted`.

- [x] covered (IntegrationTests): `text/plain`, `multipart/form-data`, `application/x-www-form-urlencoded`, and a missing
  `Content-Type` are answered 415 by `/api/query`, `/stream`, `/batch`, and `/attachment`
- [x] covered (IntegrationTests): `application/json; charset=utf-8` and bare `application/json` are still accepted
- [x] covered: `ScryClient` and the explorer already send `application/json` (pin it, since the server will now depend on it)
- [x] docs: `docs/security.md` "CORS, CSRF, TLS" — say what the endpoint enforces and what it leaves to the host


### F4 — MEDIUM — `[Sensitive]` on an overridden property is dropped by the server but kept by the generator (confirmed)

`Member.Sensitive` reads the attribute with `inherit: false`
([Member.cs:50](Scry.Server/Member.cs#L50)), and its comment says this matches the generator, which "carries a type's
own attributes and nothing inherited". It does not: `MetadataModelReader.DeclaredProperties` merges the attributes of
every declaration of an overridden name ([MetadataModelReader.cs:277-306](Scry.SourceGenerator/MetadataModelReader.cs#L277-L306)),
so a `[Sensitive]` on a base's virtual property reaches the generated model and the generator's stamp, while the server
sees neither. `[QueryIgnore]` on the same shape is read with `inherit: true`
([Schema.cs:1213](Scry.Server/Schema.cs#L1213)) and the two sides agree there.

Consequences, all reproduced on a `MarkedBase { [Sensitive] virtual Pin }` / `[Queryable] Overrider : MarkedBase
{ override Pin }` model: a URL-borne query comparing `Pin` against a constant is answered `200` rather than refused with
`requiresBody`; a response projecting `Pin` is storable; and the server's stamp omits the `~sensitive` line the
generator's includes, so every client built from such a model reports itself stale.

Fix: read `[Sensitive]` on properties with `inherit: true` (matching `[QueryIgnore]` and the generator), and correct the
comment. Type-level `[Sensitive]` can stay declared-only on both sides.

**Fixed:** `Member.Sensitive` reads the attribute through the override chain (matching `[QueryIgnore]` and the generator), comment corrected. `Audited.Reviewer`/`AuditTrail` added to the test model, so `LockstepTests` pins the agreement; `SensitiveSchemaTests.AMarkedBasePropertyIsMarkedThroughItsOverride`, `SensitiveOverrideTests`, and `SecurityTests.RejectsAnIgnoredBasePropertyThroughItsOverride` pin the behaviour.

- [x] covered (Scry.Tests `SensitiveSchemaTests`): a marked base property overridden without the attribute is sensitive
- [x] covered (Scry.Tests): the URL refusal and the `no-store` both hold for such a member
- [x] covered (TestModel + `LockstepTests`): a base with a `[Sensitive]` virtual and a `[QueryIgnore]` virtual, both
  overridden without the attribute, so `GeneratorStampMatchesServerStamp` pins the agreement
- [x] covered (server side, no lockstep): `[QueryIgnore]` on an overridden base property still hides the member —
  reproduced passing in the scratch project; add the same test to `SecurityTests`


### F5 — MEDIUM — nothing bounds the breadth of an expression or the number of correlated subqueries in one request (by inspection)

`MaxExpressionDepth` bounds nesting and `MaxProjectionMembers` bounds width, but a predicate's total node count is
bounded only by the host's request-body limit (30 MB on Kestrel by default). A flat `OrElse` chain of thousands of
comparisons, a `Where` carrying thousands of `SubqueryNode`s over `Order.Lines`, or thousands of `In` calls each at
`MaxInValues`, is accepted and handed to the provider as one statement. `docs/security.md` §6 says the limits bound the
shape of a query, not its cost, and that is true — but the count of nodes is shape, in the sense the pipeline length and
projection width already are.

Fix: add `MaxExpressionNodes` (per request, counted across every expression the validator walks) and a separate cap on
`SubqueryNode` + `InSourceNode` occurrences, both checked in `ValidateExpr`/`ValidateHaving` where the depth already is.

**Fixed:** `RequestBudget` counts every expression node and every correlated subquery across the whole request before the validator walks it; `ScryOptions.MaxExpressionNodes` (4096) and `MaxCorrelatedSubqueries` (64). Pinned by `ValidatorLimitTests.TheExpressionNodeCountIsBounded`, `...IsCountedAcrossOperators`, `TheCorrelatedSubqueryCountIsBounded`, `AMembershipTestCountsAsACorrelatedSubquery`.

- [x] covered (`ValidatorLimitTests`): a predicate over the node cap is refused before anything is rebound
- [x] covered: the cap is counted across every operator (root predicate, terminal predicate, join inner side, set operand,
  HAVING, grouped projection, nested projection), since a per-operator count is a per-operator budget
- [x] covered: N correlated subqueries in one predicate over the subquery cap are refused
- [x] decided per call, pinned (`ValidatorLimitTests.TwoContainsSetsEachAtTheLimitAreAccepted`; the node budget bounds the sum): `MaxInValues` reached through several `In` calls in one predicate — decide and pin whether it is per call
  (today) or per request
- [x] covered: depth `ValidatorLimitTests.ExpressionNestingIsBounded` and `...InAHavingClause`


### F6 — LOW/MEDIUM — the keyset cursor carries the ordering-key values in the clear (confirmed)

`CursorCodec.Encode` writes `base64url(json).base64url(hmac)` ([CursorCodec.cs:16](Scry.Server/CursorCodec.cs#L16)):
signed, not encrypted. The payload is the last row's ordering-key values plus the appended primary key. Ordering by a
`[Sensitive]` member is allowed from a URL (nothing about the value leaves, says `SensitiveAttribute`), and a page
ordered by one without projecting it is a storable response — yet its cursor contains that member's value for the last
row, and the next page carries it back in the `q=` URL, into every access log the `[Sensitive]` rule exists to keep it
out of. Repro: a page ordered by `Name` produced a cursor decoding to `{"keys":[{"value":"Ann",...},...]}`.

Fix: either encrypt the cursor (AES-GCM under the signing key, which also makes it opaque as the contract says), or have
`SensitiveWalk` treat a `PageOp` following an ordering over a sensitive member as `InConstant` + `InProjection`.

**Fixed:** cursors are sealed with AES-GCM under a key derived from `CursorKey` (`base64url(nonce || ciphertext || tag)`), so the key values never travel in the clear. Pinned by `CursorCodecTests.DoesNotCarryTheKeyValuesInTheClear`, `SealsTheSameValuesDifferentlyEachTime`, and the tamper/other-key tests over the new format; `docs/paging.md` updated.

- [x] decided and pinned: a page ordered by a sensitive member keeps the URL, since the sealed cursor carries nothing
  of the value — `SensitiveTransportTests.PagingByAMarkedMemberKeepsTheUrl` with `CursorCodecTests.DoesNotCarryTheKeyValuesInTheClear`
- [x] covered (`CursorCodecTests`): a cursor does not contain the key values as plaintext (once encrypted)
- [x] covered: cursor integrity — `CursorCodecTests.RefusesATamperedPayload`, `RefusesACursorSignedWithAnotherKey`,
  `RefusesAMalformedCursor`; `SecurityTests.RejectsInvalidPagingCursor`, `RejectsCursorOnUnorderedQuery`


### F7 — LOW — a `null` element inside a wire array is a 500, not a 400 (confirmed)

`RespectNullableAnnotations` refuses a null property, but not a null *element* of `IReadOnlyList<T>`. `"pipeline":[null]`,
`"keys":[null]`, `"arguments":[null]`, `"parts":[null]`, `"result":[null]`, `"innerOps":[null]`, `"operandOps":[null]`,
and a batch `"queries":[null]` all deserialize, and the validator dereferences the null while composing its own
rejection message ([QueryValidator.cs:370](Scry.Server/QueryValidator.cs#L370),
[QueryValidator.cs:731](Scry.Server/QueryValidator.cs#L731), [QueryValidator.cs:610](Scry.Server/QueryValidator.cs#L610)).
Nothing leaks — the 500 body is fixed — but the outcome is recorded as `Failed` rather than `Rejected`, so a client can
fill the failure metric and the audit trail with `NullReferenceException` at will, which is exactly the signal the doc
suggests alerting on. Reproduced for a pipeline entry, a group key, and a call argument.

Fix: refuse null elements in `ScryJson` (a modifier on the list contracts, or a converter), or null-check in the validator
before the `switch` and reject with a message.

**Fixed:** `NonNullElementsConverterFactory` refuses a null element of any request array at the JSON layer. Pinned by `WireSerializationTests.ANullArrayElementFailsClosed` (seven arrays), `ANullBatchEntryFailsClosed`, `ANullAttachmentKeyFailsClosed`.

- [x] covered (`WireSerializationTests`): each of the eight arrays above with a null element fails deserialization with a
  message naming the member
- [x] covered (`BatchTests`): a null batch entry is a per-entry 400, never a 500


### F8 — LOW — an out-of-range integer for `SetKind` reaches `Expression.Call` and faults (confirmed)

`JsonStringEnumConverter` accepts integers, so `"kind": 999` deserializes. The validator never checks `SetKind`; the
executor spells the method from it ([QueryExecutor.cs:587](Scry.Server/QueryExecutor.cs#L587)) and `Expression.Call`
throws `InvalidOperationException` outside the builder's catch — a 500 recorded as `Failed`. By inspection the other enums
fail closed as 400s: `BinaryOp`/`UnaryOp` (builder `default` arms), `KnownFunction` (`Arity`), `SubqueryFn`,
`AggregateFn`, `JoinKind` (`JoinMethod`), `JoinSide` (validator), `StringMatch` (`Collation`), `ClrTypeTag` (`TagToType`
falls back to string).

Fix: `Enum.IsDefined` every wire enum in the validator (one helper), or configure the enum converter with
`allowIntegerValues: false` — the latter is a wire change and needs the client checked.

**Fixed:** the validator holds every request enum to its defined values (`EnsureDefined`), since integers must stay readable in payloads. Pinned by `SecurityTests.RejectsAnUndefinedEnumValue` (nine enums) and `RejectsAnUndefinedSubqueryFunction`.

- [x] covered: `"kind": 999` is a 400
- [x] covered: one test per wire enum with an undefined integer, asserting 400 (pins the fail-closed arms named above)
- [x] decided case-sensitive (`TolerantEnumConverter` parses names exactly; `WireSerializationTests.AnEnumNameInTheWrongCaseFailsClosed`) — was: enum *names* on the wire — the converter reads them case-insensitively, so
  `"op":"equal"` and `"op":"Equal"` are two byte-strings for one query (ETag, `q=`, and audit fingerprints diverge)


### F9 — LOW — validation messages name CLR types where the wire name was chosen to hide them (confirmed)

`Property '{name}' is not allow-listed on '{currentType.Name}'` ([QueryValidator.cs:1491](Scry.Server/QueryValidator.cs#L1491))
and `'{x}' does not derive from '{rootType.Name}'` use the CLR name. For a source declared
`[Queryable(Name = "Region")] class SalesRegion`, or the scratch `[Queryable(Name = "Renamed")] class SecretClrName`, a
probe with an unknown member answers `... not allow-listed on 'SecretClrName'.` `docs/security.md` §7 says a message names
nothing beyond what the allow-list implies; a CLR name is beyond it. Complex types have no wire name at all and are named
by CLR name in the same message.

Fix: resolve the wire name through `schema.TryGetSourceForType` and fall back to the model name (`{Name}QueryModel`) that
introspection already publishes.

**Fixed:** `Schema.WireName` names a source by its wire name and any other type by its model name; every validator and builder message goes through it. Pinned by `SecurityTests.RejectionNamesTheWireNameNotTheClrType`.

- [x] covered (`SecurityTests`): a rejection on a renamed source never contains the CLR type name
- [x] covered: `SecurityTests.RejectionOnANarrowingNamesTheWireName`, `RejectionOnAComplexTypeNamesItsModel` (a
  complex type is named as introspection publishes it, `{Name}QueryModel`)


### F10 — LOW — a policy that DI cannot supply is constructed reflectively, per request (by inspection)

Row, attachment, and cached policies fall back to `Activator.CreateInstance`
([QueryExecutor.cs:1032](Scry.Server/QueryExecutor.cs#L1032), [AttachmentPolicy.cs:26](Scry.Server/AttachmentPolicy.cs#L26),
[CachedRowPolicyAdapter.cs:274](Scry.Server/CachedRowPolicyAdapter.cs#L274)). A policy with a parameterless constructor and
an unregistered dependency is silently built without it; one without a parameterless constructor is a `MissingMethodException`
— a 500 on every query of that source, after startup passed. The navigation probe exercises only policies reached by a
traversal, so a root-only policy is never constructed before the first request.

Fix: at `MapScry` startup, resolve every registered policy type once (DI or a parameterless constructor) and refuse to
start otherwise; consider dropping the `Activator` fallback for a type that has any constructor parameter.

**Fixed:** `ScryProcessor.EnsurePoliciesResolvable` runs at `MapScry` startup over every row, attachment, and cached policy. Pinned by `PolicyResolutionTests`.

- [x] covered: a policy with constructor dependencies and no DI registration is a startup failure naming the policy
- [x] covered: a policy registered scoped in DI is resolved from the request scope (pins the intended path)


### F11 — LOW — the `ETag` embeds the raw `CacheScope` and freshness token (by inspection)

`QueryEtag.Etag` writes `"{stamp}-{freshness}-{query}-{scope}"` ([QueryEtag.cs:119](Scry.Server/QueryEtag.cs#L119)).
The scope is "a tenant, a principal" per `ScryOptions.CacheScope`; the freshness token is the database's log position.
Both are handed to the caller verbatim on every 200 and 304. Hashing the pair (as the query already is) costs nothing
and keeps identifiers and write-activity timing out of a header a browser stores for as long as it caches.

**Fixed:** the freshness token and the cache scope are fingerprinted into the `ETag` the way the query is. Pinned by `QueryEtagTests`.

- [x] covered (`samples/Sample.Tests` `ConditionalQueryTests`): the `ETag` does not contain the `CacheScope` string or the
  freshness token verbatim
- [x] covered: a rejected query carries no `ETag` — by code (`OnStarting` checks 200 and `no-store`); add a test


### F12 — LOW — the cursor's order stamp does not include the flatten (by inspection)

`CursorCodec.OrderStamp(source.Name, keys)` stamps the **root** source and the key paths. `Root.SelectMany(C).OrderBy(Name).Page`
and `Root.OrderBy(Name).Page` stamp identically when both types expose `Name` and `Id`, so a cursor from one resumes the
other and seeks the element rows past a root row's values: a plausible, wrong page. Not a leak (both queries are
policy-filtered), but the stamp exists to catch exactly this.

**Fixed:** the ordering stamp (`scry-order-v2`) now carries every step that changed what the rows are — each `SelectMany` path and `OfType` target, in order — beside the source and keys. Pinned by `CursorBindingTests.RejectsACursorFromTheRootOnAFlattenedQuery`, `RejectsACursorFromAFlattenedQueryOnTheRoot`, `ResumesAFlattenedOrdering`, `RejectsACursorFromANarrowedQueryOnTheBase`, and `CursorCodecTests.StampsAnOrderingByItsKeysAndDirections`.

- [x] covered (`CursorBindingTests`): a cursor issued for the root is refused by the flattened query and vice versa
- [x] fix: include the `SelectMany` path (and `OfType` target) in the stamped canonical form


### F13 — LOW — a cold cached-policy scope loads the whole table per new scope key (by inspection)

`CachedRowPolicyAdapter.Undecided` reads `Set<T>().AsNoTracking()` with no watermark
([CachedRowPolicyAdapter.cs:287](Scry.Server/CachedRowPolicyAdapter.cs#L287)) and `Decide` runs the host's `Allow` per row
under the scope's gate. `MaxCachedPolicyKeys` is checked in `Bounded` *after* deciding. An authenticated caller whose scope
key is new — every user, on a per-user scope — costs one full materialization of the table; many first-time callers cost
that many, concurrently, since the gate is per scope. The design is documented; the missing piece is a bound on the work
rather than on the result.

**Fixed:** `ScryOptions.MaxCachedPolicyRows` (null by default) bounds the work of a refresh: the undecided rows are counted before they are read, and a scope past the bound is refused from the count naming the option. `MaxCachedPolicyKeys` keeps bounding the result. Pinned by `CachedPolicyTests.ATooLargeColdScopeIsRefusedBeforeItsRowsAreRead` (the policy is never asked); `docs/policies.md` §"Cost and shape" now says the cold cost is the table, per scope key.

- [x] covered: `MaxCachedPolicyRows` refuses before the rows are materialized, with a `Count` first, naming the option
- [x] docs: `docs/policies.md` — say what a cold scope costs and that the cost is per scope key
- [x] covered: `CachedPolicyTests.ATooLargeAllowedSetIsRefusedRatherThanSentToTheDatabase` (after the fact)


### F14 — INFO — request size, `In` lists, and JSON depth rely on host limits

`ReadBody` is bounded by Kestrel's `MaxRequestBodySize`; a 30 MB `In` list is deserialized whole before `MaxInValues`
refuses it; JSON nesting is bounded by `JsonSerializerOptions.MaxDepth` (64 by default, never set explicitly) and only
then by `MaxExpressionDepth`. All fine, and all undocumented.

**Fixed (pinned and documented):** `HostLimitTests` hosts `MapScry` on Kestrel with a `RequestSizeLimit` on the builder it returns and shows all four POST endpoints answer 413 from the host, and a body within the limit is served; `HttpRoundTripTests.ADeeplyNestedBodyIsRejected` shows a document nested past the reader's 64 levels is a 400 naming the depth. `docs/security.md` §6 names the three host bounds and recommends the size limit.

- [x] covered (IntegrationTests): a body over a configured `MaxRequestBodySize` is a 413, and a JSON document nested
  past 64 is a 400 rather than a stack fault
- [x] docs: `docs/security.md` §6 — name the host limits the endpoints depend on, and recommend a size on `MapScry`


### F15 — INFO — a client can produce `Failed` outcomes on demand

Division by a client constant, `Int32From` over non-numeric text, `BytesElementAt` past the end: each is a provider error
at execution, a fixed 500, and a `Failed` audit entry carrying the SQL exception text. Contained, but `docs/security.md`
suggests alerting on rejections and `docs/observability.md` on failures; a client can make either noisy.

**Fixed (documented):** `docs/security.md` §7 and `docs/observability.md` (the `Error` bullet) both say a client can produce a `Failed` outcome at will and that `Error` carries the provider's text.

- [x] docs: say that `Failed` is client-triggerable and that the audit `Error` text is provider text
- [x] covered: the response is the fixed message — `ResponseSpillTests.AFailureBeforeTheWatermarkIsStillAnError`,
  `ObservabilityTests.AuditForFailed`


### F16 — INFO — explorer share links deliver arbitrary C# that runs in the opener's browser on Run

`ShareLinkCodec.Decode` loads the fragment into the editor (`App.razor.cs:112-123`) and nothing runs until the user
presses Run — good. Pressing it compiles and executes the snippet in the WASM sandbox with the BCL and `Scry.Client`
referenced, so a crafted link can, with one click, run `HttpClient` calls with the opener's same-origin credentials.
The explorer is Development-only by default and the doc tells hosts to guard it, which bounds this.

**Pinned:** `UiSnapshotTests.ExplorerOpensASharedLinkWithoutRunningIt` opens a shared link in a fresh page and asserts the query is loaded, no request reached the query endpoint, and neither the wire strip nor a result table exists.

- [x] covered (`Sample.Tests`): opening a `#q=` link never runs the query; the wire strip and result pane stay empty
- [ ] consider: a banner on a query that arrived via share link, cleared once the user edits or runs it (not done —
  a product decision rather than a gap; the link cannot run anything without a click)
- [x] covered: `UiSnapshotTests.ExplorerSharesAQueryByLink`, `ExplorerIgnoresAMalformedShareLink`


### F17 — INFO — a policy-supplied attachment `ContentType` is not validated

`ValidateContentType` checks the declared attribute at startup; `ScryAttachmentContext.ContentType` set by a policy
reaches the header unchecked. Kestrel refuses a value with a line break, so the failure mode is a 500 rather than a split
response, and the value is host code — but the model's declaration gets a startup check and the override gets none.

**Fixed:** `PlanAttachment` holds a policy's `ContentType` to the same media-type rule the startup check applies to the declaration (`Schema.IsMediaType`); a value that is not one is a fault naming the policy — the fixed 500, the real message audited — and never a header. Pinned by `AttachmentFetchTests.AReplacementThatIsNotAMediaTypeFaults` and, for the override path itself, `APolicyMayRelabelTheBytes`; `docs/attachments.md` says so.

- [x] covered: a policy override that is not a media type is a fault with the fixed message, never a header


### F18 — INFO — the `scry.member` trace tag carries the raw client string

`QueryRecorder.StartActivity` tags the attachment member as sent ([QueryRecorder.cs:88](Scry.Server/QueryRecorder.cs#L88)),
where the source tag is only ever a schema name. Trace backends are not metrics, so cardinality is a cost rather than a
break; still, tag the member only when the schema knows it, as `Source` already does.

**Fixed:** `QueryRecorder` tags `scry.member` with the schema's name for the member only where the source knows it as an attachment, and `(unknown)` otherwise — the rule `scry.source` already follows. Pinned by `ObservabilityTests.ActivityForAnAttachment` and `ActivityForAnUnknownAttachmentMember`.

- [x] covered (`ObservabilityTests`): an unknown attachment member is tagged `(unknown)`


### F19 — INFO — attachment timing distinguishes a policy refusal from a missing row

`PlanAttachment` returns before touching the database when the policy refuses, and after a round trip when the row is
absent. The status is the same 404; the latency is not. The comment says an unauthorized caller learns "not even how long
a lookup took", which is the opposite of what happens. A policy that decides on the key alone is the case that matters.

**Fixed (documented):** the comment in `PlanAttachment` and `docs/attachments.md` §Security now say a refusal answers sooner than a missing row, and that a policy which must not give that away reads the row through `Db` before deciding. The row is not read regardless: that would make every refused request cost a lookup, which is the cheaper attack.

- [x] docs: soften the claim in `QueryExecutor.PlanAttachment` and `docs/attachments.md`


### F20 — LOW — an opted-in type the context does not map faults on every request (found by the matrix)

`Schema.ValidateAgainstModel` leaves a source-annotated type absent from the EF model alone, by design
(`Schema.cs`: "[Queryable] is deliberately allowed on types that carry no DbSet"). A client can name such a source —
introspection advertises it — and every query of it is an `InvalidOperationException` from `Set<T>()`: a fixed 500,
recorded as `Failed`, on demand. `TestModel` carries two (`DepartmentHeadcount`, `SalesRegion` as `Region`), which is how
`FunctionMatrixTests` found it. Nothing leaks; the cost is a client-drivable failure count and a host mistake that
surfaces per request rather than at startup.

- [x] fixed: refused at `MapScry` startup by `ScryProcessor.EnsureSourcesMapped` unless `AllowUnmappedSources` waives it for an assembly serving several contexts, where a query naming one is a 400 `Unknown source` rather than a fault (`SourceMappingTests`; `docs/server.md` lists it) — was: refuse at startup or answer `Unknown source` (400); either
  ends the per-request fault; the test model's unmapped types would then need a `DbSet` or a `[QueryablePoco]`
- [x] covered (as a skip): `FunctionMatrixTests.ScalarMembers` excludes sources the context does not map


## Documentation corrections

- [x] `docs/attachments.md` and `Scry.Annotations/AttachmentAttribute.cs` — the "no way to navigate a browser to one"
  claim (F2)
- [x] `docs/security.md` §5 — a flatten resets the policy chain to the element's (F1)
- [x] `docs/security.md` "What Scry does not do" — CSRF: what `Content-Type` enforcement covers (F3)
- [x] `docs/security.md` §7 — messages name a source's wire name, never its CLR name (F9)
- [x] `Scry.Server/Member.cs` — the comment on `Sensitive` (F4)
- [x] `docs/paging.md` — the cursor is sealed (F6)


## Scenarios checked and found sound — test coverage

Each line is a scenario that was checked against the code. `[x]` names the test that pins it; `[ ]` is a gap to fill.


### Wire deserialization (`Scry.Wire`)

- [x] unknown `$type` fails closed — `WireSerializationTests.UnknownDiscriminatorFailsClosed`
- [x] malformed JSON fails closed — `WireSerializationTests.MalformedJsonFailsClosed`, `MalformedAttachmentRequestFailsClosed`
- [x] a required member omitted or explicitly null is refused —
  `AnOmittedRequiredMemberFailsClosed`, `AnExplicitNullForARequiredMemberFailsClosed`, `ANullRootFailsClosed`,
  `AnOmittedRootFailsClosed`, `AnAttachmentRequestWithoutKeysFailsClosed`
- [x] optional members read back as null — `OmittedOptionalMembersReadBack`, `AnAttachmentKeyWithoutAValueReadsBack`
- [x] one spelling per path and per projection member — `PathNamingOneMemberAsAnArrayFailsClosed`, `ProjectionMemberEncodingTests`
- [x] a newer wire version is refused (request, batch, attachment, response, stream marker) —
  `SecurityTests.RejectsUnsupportedWireVersion`, `BatchTests.UnsupportedWireVersionRejectsTheWholeBatch`,
  `AttachmentFetchTests.NewerVersionIsRejected`, `WireSerializationTests.NewerResponseVersionFailsClosed`,
  `StreamReadTests.RefusesANewerWireVersionOnTheOpeningMarker`
- [x] every wire type resolves from the generated metadata — `WireMetadataTests`
- [x] decided refused (query, batch, attachment; `SecurityTests.RejectsAWireVersionBelowOne`, `BatchTests.AWireVersionBelowOneRejectsTheWholeBatch`, `AttachmentFetchTests.AVersionBelowOneIsRejected`): `"version": 0` and a negative version — decide and pin (accepted today)
- [ ] add: `$type` not first in the object is refused (STJ default; pin it, since `AllowOutOfOrderMetadataProperties`
  would silently change it)
- [x] covered: unknown members on a request are refused at every level — `WireSerializationTests.AnUnknownMemberFailsClosed`,
  `AnUnknownMemberOnABatchFailsClosed`, `AnUnknownMemberOnAnAttachmentRequestFailsClosed` (a response stays lenient)
- [x] decided refused (`ScryJson.Options.AllowDuplicateProperties = false`; `WireSerializationTests.ADuplicatePropertyFailsClosed`)
- [ ] add: a byte-for-byte `q=` decode failure (bad base64url, valid base64url of non-JSON, valid JSON of a non-request)
  is a 400 with `no-store` — `HttpRoundTripTests.MalformedUrlQueryIsRejected` covers the first only
- [x] decided refused in the validator, for projections and join results (`SecurityTests.RejectsAProjectionNamingAMemberTwice`, `RejectsAJoinResultNamingAMemberTwice`; the two golden cases that pinned the overwrite are gone): the `ProjectionMembersConverter` refuses a duplicate member name in one projection (accepted today; the shaped
  row overwrites, and the fast writer's output for two members of one name is unpinned)


### Validator: allow-list and traversal

- [x] unknown root — `SecurityTests.RejectsUnknownRoot`; via join/set/membership/OfType —
  `JoinTests.JoiningAnUnknownSourceIsRejected`, `SourceMembershipTests.MembershipAgainstAnUnknownSourceIsRejected`,
  `OfTypeTests.RejectsNarrowingToATypeThatIsNotOptedIn`
- [x] `[QueryIgnore]` member in a predicate, projection expression, aggregate terminal, subquery, join side, set side,
  membership source, JSON array — `SecurityTests.RejectsIgnoredProperty`, `RejectsIgnoredPropertyInsideAProjectionExpression`,
  `RejectsAggregateTerminalOverAnIgnoredMember`, `CollectionSubqueryTests.AnIgnoredMemberStaysHiddenInsideASubquery`,
  `JoinTests.AnIgnoredMemberStaysHiddenOnEitherSideOfAJoin`, `SetOperationTests.AnIgnoredMemberStaysHiddenOnTheOtherSide`,
  `SourceMembershipTests.AnIgnoredMemberStaysHiddenOnTheOtherSource`, `ComplexCollectionTests.AnIgnoredMemberStaysHiddenInsideAJsonArray`
- [x] traversal through a scalar, a collection, a JSON array — `SecurityTests.RejectsTraversalThroughScalar`,
  `CollectionSubqueryTests.TraversingThroughACollectionIsRejected`, `ComplexCollectionTests.TraversingThroughAJsonArrayIsRejected`
- [x] projecting a collection, a JSON array, a collection of values — `CollectionSubqueryTests.ProjectingACollectionIsRejected`,
  `ComplexCollectionTests.ProjectingAJsonArrayIsRejected`, `PrimitiveCollectionTests.ProjectingACollectionOfValuesIsRejected`
- [x] an un-opted-in collection is invisible — `CollectionSubqueryTests.AnUnOptedInCollectionStaysInvisible`,
  `PrimitiveCollectionTests.AnUnOptedInCollectionOfValuesStaysInvisible`
- [x] complex member as a scalar; ignored complex member — `SecurityTests.RejectsComplexMemberAsScalar`, `RejectsIgnoredComplexMember`
- [x] attachment named anywhere — `SecurityTests.RejectsAttachmentIn*`, `RejectsAttachmentThroughNavigation`,
  `SequenceReadTests.AnAttachmentAnswersNoneOfThem`, `AttachmentTests.QueryNamingTheAttachmentIsRejected`
- [x] element node outside a value subquery; member of a value element —
  `PrimitiveCollectionTests.ReadingAnElementOutsideASubqueryIsRejected`, `ReadingAnElementInsideACollectionOfRowsIsRejected`,
  `ReadingAMemberOfAValueElementIsRejected`; flattening values — `FlatteningACollectionOfValuesIsRejected`
- [x] derived member without narrowing; narrowing to same/unrelated/base — `OfTypeTests` (five rejection tests)
- [x] previous names resolve and never widen — `PreviousNamesTests` (eight tests)
- [ ] add: member and source lookups are ordinal — `"name"` is not `"Name"`, `"employee"` is not `"Employee"`
- [ ] add: the startup refusals for `[PreviousNames]` (blank, current name, claimed twice, on an unexposed member, on
  a complex type, on an un-opted-in type) — `Schema.RegisterSourcePreviousNames`/`RegisterMemberPreviousNames` have no tests
- [ ] add: `OfType` to a sibling after narrowing (`Asset.OfType(Vehicle).OfType(Building)`) is refused
- [ ] add: `SelectMany` over a complex-typed collection then `OfType` is refused (a complex type is never a source)


### Validator: pipeline shape

- [x] `ThenBy` without `OrderBy`; operator after terminal; `Last`/`Reverse` without ordering —
  `SecurityTests.RejectsThenByWithoutOrderBy`, `RejectsOperatorAfterTerminal`, `RejectsLastWithoutOrdering`,
  `ExpandedOperatorTests.ReverseWithoutOrderingIsRejected`
- [x] grouped projection non-key; aggregate outside a group; group key range —
  `SecurityTests.RejectsGroupedProjectionReferencingNonKey`, `RejectsAggregateWithoutGroupBy`,
  `ExpandedOperatorTests.ANonKeyMemberInAGroupedProjectionIsStillRejected`, `HavingOverANonKeyMemberIsRejected`,
  `ComputedGroupKeyTests.RejectsAGroupKeyBeyondTheKeysTheQueryHas`, `RejectsAGroupKeyOutsideAGroupedQuery`
- [x] a second `Select`, `SelectMany`, `Join`, set operation — `GroupByResultSelectorTests.ASelectAfterTheResultSelectorIsASecond`,
  `SelectManyTests.RejectsASecondFlatten`, `JoinTests.OperatorsAfterAJoinAreRejected`, `SetOperationTests.OperatorsAfterASetOperationAreRejected`
- [x] terminal predicate after `Select` — `SecurityTests.RejectsTerminalPredicateAfterSelect`
- [x] `Distinct` rules — `SecurityTests.RejectsOrderByAfterDistinctOnAnUnprojectedMember`, `RejectsPagingAfterDistinct`,
  `RejectsCountingADistinctQueryBeyondTheRowArity`, `ExpandedOperatorTests.PagingADeduplicatedQuery*`, `GroupedDistinctTests`
- [x] right join outer side — `JoinTests.RightJoinRejectsANarrowedOuterSide`, `...ToDerivedOuterSide`, `...APoliciedOuterSide`
- [x] side pipelines — `SidePipelineTests` (grammar, both spellings, unbounded ordering), `ValidatorLimitTests` (empty,
  negative skip, take bounds), `JoinTests.UnsupportedOperatorsOnTheInnerSideAreRejected`,
  `SourceMembershipTests.AnUnsupportedOperatorOnTheOtherSourceIsRejected`
- [x] composite keys — `CompositeJoinKeyTests`; aggregate shapes — `StringJoinAggregateTests`, `FilteredAggregateTests`
- [x] subquery/membership nesting — `CollectionSubqueryTests.ASubqueryInsideASubqueryIsRejected`,
  `AMembershipTestInsideASubqueryIsRejected`, `ASubqueryWrappedInACollationIsStillNested`,
  `SourceMembershipTests.ANestedMembershipTestIsRejected`, `ASubqueryInsideAMembershipTestIsRejected`,
  `AMembershipTestInsideASubqueryInTheValueIsRejected`
- [ ] add: a second `GroupBy`; `GroupBy` after `Select`; `GroupBy` without a following `Select`; `Where`/`OrderBy`/
  `OfType`/`SelectMany`/`Join` after `Select` — each rule exists in `ValidatePipeline` and none is pinned by name
- [ ] add: a terminal predicate after a `Join` (`Count(pred)`, `First(pred)`) is refused
- [ ] add: `Page` after `Join` or a set operation is refused
- [x] decided accepted on both (`ValidatorLimitTests.ASetOperandTakeOfZeroIsAccepted`, `ASetOperandTakeCannotBeNegative`): `Take(0)` at the root is accepted while a side `Take(0)` is refused — decide whether the asymmetry is meant
- [x] pinned as accepted (`ValidatorLimitTests.ASkipOfIntMaxIsAccepted`): `Skip` has no upper bound — pin `Skip(int.MaxValue)` as accepted (or bound it)


### Validator: limits

- [x] pipeline length (root and both sides), expression depth (predicate and HAVING), navigation depth (path and
  nesting), projection width (flat, nested, join), `Take`/page/side-take against `MaxPageSize`, `MaxInValues` in a
  predicate/HAVING/grouped projection, group-by arity, distinct arity, batch size, stream rows —
  `ValidatorLimitTests`, `SecurityTests` (limits), `BatchTests.OverMaxBatchSizeRejectsTheWholeBatch`,
  `StreamingTests.EndsAStreamThatExceedsTheRowLimitWithAFailure`
- [x] covered: F5's node and subquery caps (`ValidatorLimitTests`)
- [ ] add: `MaxNavigationDepth` is measured per path, so a subquery predicate's path plus its owner's path reaches
  twice the limit — pin the effective bound
- [ ] add: `MaxProjectionMembers` counts a join's `Result` and a set operand's projection (the operand is validated
  through `ValidateProjection` and so counted; the join has its own check) — pin both


### Rebinding, constants, and parameterization

- [x] constants, `In` values, skip/take, page size, and the join separator are bound, never inlined — `ParameterizationTests`,
  `StringJoinAggregateTests.TheSeparatorIsAParameter`
- [x] a constant is parsed as the member's type, not the tag's — `ClientRoundTripTests.UnsignedMemberFilters`,
  `RepresentationChangeTests`, `HttpRoundTripTests.DriftedRebindFailureIsAttributedToStaleClient`
- [x] an unparseable constant, key, or cursor value is a 400 — `AttachmentFetchTests.UnparseableKeyIsRejected`,
  `RepresentationChangeTests.TighteningRejectsTextThatDoesNotParseAtRebind`, `SecurityTests.RejectsInvalidPagingCursor`
- [x] the collation is server configuration and never on the wire — `CollationTests.TheCollationIsNeverCarriedOnTheWire`,
  `AnUnconfiguredCollationIsRejected`, `AMalformedCollationIsRefusedAtStartup`
- [x] type mismatches are rejections, not faults — `TypeMismatchTests`, `SecurityTests.RejectsDatePartOnNonTemporalMember`,
  `NumericPromotionTests.ASumOverANonNumericMemberIsRefused`, `HasFlagTests.RejectsHasFlagOverSomethingNotAnEnum`,
  `SignTests.RejectsTheSignOfSomethingNotNumeric`, `MinMaxTests.RejectsSomethingNotNumeric`, `CompareToTests.RejectsSomethingWithoutAnOrdering`,
  `ToStringTests.RejectsReadingAnEnumAsText`, `TextConversionTests.*RefusedByTheServer`, `AngleConversionTests.RejectsSomethingNotNumeric`
- [x] unknown enum value name — `PreviousNamesTests.UnknownEnumValueIsRejected`
- [x] covered (`FunctionMatrixTests`, generated from the schema; found F20): a systematic matrix — every `KnownFunction` applied to every scalar member type of `TestModel`, and every
  `BinaryOp`/`UnaryOp` over every pair of scalar types, asserting the outcome is success or `ScryValidationException`
  and never another exception type (the builder's two catch arms are the only guard, and each new function is a new gap)
- [ ] add: `CollateNode` over a non-string target (an `int` member) is a 400, not a provider fault
- [x] decided refused (`ExpressionBuilder.ParseValue` holds a parsed enum to a defined value or flag combination; `SecurityTests.RejectsAnEnumConstantSpelledAsAnUndefinedInteger`): an enum constant spelled as an undefined integer (`"999"`) — `Enum.Parse` accepts it; decide and pin
  (accepted today and matches nothing; `HasFlag` with it likewise)
- [ ] add: a `ConstNode` on both sides of a comparison (`1 == 1`) — accepted; pin as intended
- [x] decided: a negative constant index is refused in the validator (`SecurityTests.RejectsANegativeIndex`), past the end stays the provider's — `BytesElementAt` with a negative or past-the-end index and `StringSubstring` with a negative start are
  provider faults (fixed 500) rather than 400s — decide whether to validate the constant range


### Row policies

- [x] root policy before client filters; attribute and programmatic; override; inheritance (five tests) —
  `ExecutionTests.PolicyScopesRowsBeforeClientFilter`, `ReturnableWithAttributeScopesRows`, `AddPolicyOverridesReturnableWithAttribute`,
  `PolicyInheritanceTests`
- [x] join inner side, group join, right join inner, set operand, membership set, batch entries, streams —
  `JoinTests.TheInnerSourcePolicyIsAppliedBeforeTheJoin`, `RightJoinAppliesTheInnerSidePolicy`,
  `GroupJoinTests.AppliesTheInnerSidePolicyBeforeCounting`, `SetOperationTests.TheOtherSourcePolicyIsAppliedBeforeCombining`,
  `SourceMembershipTests.MembershipIsPolicyFilteredOnTheOtherSource`, `BatchTests.EveryEntryIsPolicyFiltered`,
  `StreamingTests.AppliesTheRowPolicyToAStream`
- [x] navigation into a policied source at every position, chained, probed at startup — `NavigationPolicyTests`
- [x] collections of a policied type: refuse, hide, error; complex-type policy refused — `PoliciedCollectionTests`,
  `CollectionSubqueryTests.ExposingACollectionOfAPoliciedTypeIsRefusedAtStartup`, `ComplexCollectionTests.AttachingARowPolicyToAComplexTypeIsRefusedAtStartup`
- [x] denied-row error mode at each position, never for an already-hidden row, fixed message — `DeniedRowTests`, `DeniedRowHttpTests`
- [x] SQL preview runs the policies and denies nothing — `SqlPreviewTests.APolicyIsInTheSql`, `DeniedRowTests.ShowingTheSqlRunsNothingAndSoDeniesNothing`
- [x] header-scoped policies are documented as unsafe; headers reach the policy and back — `HeaderTests`, `HttpRoundTripTests.HeadersRoundTripThroughAPolicy*`
- [x] covered: F1's flatten-then-narrow tests (`FlattenNarrowPolicyTests`)
- [x] covered: F10's constructibility test (`PolicyResolutionTests`)
- [x] covered (`FlattenNarrowPolicyTests.ARightJoinAfterAFlattenKeepsTheElementPolicy` — the narrowing stays inside the APPLY, the join stays right): `RightJoin` after `SelectMany` over a `Hide`-mode policied element — pin whether the hoisting the validator
  guards against on the outer side recurs here (the validator's `sawOuterFilter` is not set by `SelectMany`)
- [ ] add (deferred: the test model has no view or POCO hierarchy, and a derived `[QueryablePoco]` needs its own `AddPocoSource` in every fixture): a policy on a base type reached through `OfType` from a *view* or POCO root — `Retype` casts an in-memory
  or keyless query; pin that it executes rather than faults


### Cached row policies

- [x] decisions per scope, watermark, invalidation, priming, generation races, key bound, no-key refusal —
  `CachedPolicyTests`, `MemoryCachedPolicyStoreTests`
- [x] covered: F13's pre-materialization bound (`CachedPolicyTests.ATooLargeColdScopeIsRefusedBeforeItsRowsAreRead`)
- [ ] add: `ScopeKey` read from a request header is documented as unsafe — pin with a policy that does so and a test
  showing two callers with different headers share nothing *only* because the sample resolves the key from services
  (a doc-shaped test, so the warning has a name)


### Cursors and paging

- [x] signature, tamper, malformed, other key, ordering stamp (direction, source, key count), changed filter — `CursorCodecTests`, `CursorBindingTests`
- [x] cursor on unordered/grouped/distinct; page size caps and defaults — `SecurityTests`, `ExecutionTests.Paged*`
- [x] seek falls back to offset on nullable keys, views, POCOs, byte[] keys — `ClientRoundTripTests.Keyset*`, `PagingOverAByteArrayKeyIssuesNoCursor`
- [x] covered: F6 (`CursorCodecTests`) and F12 (`CursorBindingTests`)
- [ ] add: a cursor value that does not parse as the key member's type is a 400 (`BuildConstant` → `ParseValue`), and a
  null value for a non-nullable key matches nothing rather than faulting
- [ ] add: `CursorKey` set makes cursors survive a second processor; unset makes them per-process (documented,
  untested)


### HTTP endpoints

- [x] 400 with a specific message; 500 with the fixed message; `no-store` on errors; `private, no-cache` on GET;
  `no-store` on sensitive projections; stamp and URL-limit headers on every response —
  `HttpRoundTripTests` (transport tests), `UrlLimitTests`, `AttachmentTests.EveryStatusCarriesTheSchemaStamp`
- [x] streaming: rejection before the first byte; row limit as an error marker; denial before the stream starts —
  `HttpRoundTripTests.StreamingAQueryTheServerRejectsFailsBeforeAnyRowArrives`, `BinaryTransferTests.StreamOverTheRowLimit*`,
  `DeniedRowHttpTests.AStreamIsDeniedBeforeItStarts`
- [x] body reading never sizes to the declared length — `RequestBodyTests`
- [x] GET route unmapped at `QueryUrlLimit = 0`; caching refused without a scope — `UrlLimitTests`
- [x] conditional requests: 304, invalidation on write and on grant change, per-query ETags, none on a body —
  `samples/Sample.Tests/ConditionalQueryTests`
- [x] error bodies JSON-escape client strings — reproduced (`<script>`); add a test in IntegrationTests
- [x] covered: F2, F3, F11 tests (`AttachmentTests.IsServedAsADownload`, `HttpRoundTripTests.*Refused`, `QueryEtagTests`)
- [x] covered (`HttpRoundTripTests.AProviderFailureAfterTheStreamBeganEndsItWithTheFixedMessage`): a provider failure *after* the stream's begin marker ends with the fixed message, never SQL text
  (`HandleStream`'s catch; the row-limit test covers the validation-message branch only)
- [ ] add: a rejected query carries no `ETag`; a `no-store` response carries no `ETag`
- [x] decided excluded: a bare `*` never matches (`QueryEtag.Matches`; `ConditionalQueryTests.AWildcardConditionIsNotAMatch`)
- [ ] add: `HEAD` and `OPTIONS` on the query route are 405 (no handler runs, nothing is advertised)
- [ ] add: a `q=` parameter given twice is a 400 (the joined `StringValues` is not base64url)
- [x] fixed: every `ScryValidationException` message is bounded at 1024 chars (`SecurityTests.ARejectionEchoingALongClientStringIsBounded` — root, member, constant)
- [ ] add: the `RequireAuthorization` convention reaches the GET route (the attachment and POST routes are covered by
  `AttachmentTests.AuthorizationReachesTheAttachmentEndpoint`; the GET route is inserted after that list is built)


### Batch

- [x] size, version, per-entry validation/policy/audit, rejected entry isolation — `BatchTests`, `HttpRoundTripTests.BatchEntryRejectedOverHttp`
- [ ] add: a batch entry that *fails* (provider error) reports the fixed message and its own `staleClient` attribution
- [x] covered: a null entry fails the batch's deserialization closed (F7, `WireSerializationTests.ANullBatchEntryFailsClosed`)
- [ ] add: response headers written by one entry's policy apply to the whole batch response — pin as intended


### Attachments

- [x] denied, missing, and hidden are one 404; row policy applies; key parsing; wrong count; unknown member/source;
  null key; version; content types; `nosniff`; authorization convention — `AttachmentFetchTests`, `AttachmentTests`
- [x] covered: F2 (`AttachmentTests.IsServedAsADownload`), F17 (`AttachmentFetchTests`), F19 (documented)
- [ ] add: a source exposing an attachment with no policy refuses to start (`Schema.ResolveAttachmentPolicy`, untested)
- [ ] add: an attachment policy declared on a base is applied to the derived source (server side; only the explorer's
  `AttachmentLinkerTests.LinksAnInheritedAttachment` touches inheritance today)
- [ ] add: the startup refusals for `[Attachment]`/`[BinaryTransfer]` on a non-`byte[]`, on a hidden member, on both at
  once, on a view/POCO source, on a complex type, and for a malformed declared content type — none has a test
- [ ] add: a key value carrying a *different* tag than the key's type (`"tag":"String"` for an `int` key) is parsed as
  the member's type (the doc says the tag is a hint; pin it for keys as `UnsignedMemberFilters` does for constants)


### Sensitivity

- [x] constant against a marked member (root predicate, terminal predicate, type-marked, optional struct, elsewhere in
  the filter) travels as a body and is refused from a URL; projection is `no-store`; default projection likewise;
  ordering keeps the URL; a stale client retries in a body; the audit flags it —
  `SensitiveTransportTests`, `SensitiveStructTests`, `SensitiveSchemaTests`, `HttpRoundTripTests` (sensitive tests),
  `ObservabilityTests.AuditFlagsAConstantAgainstASensitiveMember`
- [x] covered: F4 (`SensitiveSchemaTests`, `SensitiveOverrideTests`) and F6 (`SensitiveTransportTests.PagingByAMarkedMemberKeepsTheUrl`)
- [ ] add: a constant against a marked member inside a join's inner predicate, a set operand's predicate, a subquery
  predicate, a membership predicate or selector, a HAVING clause, and a group key — each is walked by `SensitiveWalk`
  and none is pinned from a URL
- [ ] add: a marked member projected through a join `Result` or a set operand projection is `no-store`
- [ ] add: an unknown `Node` kind fails closed in `SensitiveWalk` (both flags set) — reachable only by adding a node
  kind; pin with a test-local subclass if the walk can be given one, otherwise document


### Explorer host and UI

- [x] explorer assets revalidate; introspection endpoint; share link; malformed share link; CSV formula neutralisation;
  XML escaping; attachment link derivation — `UiSnapshotTests`, `ResultExporterTests`, `AttachmentLinkerTests`
- [x] SQL preview refuses what the query would refuse and shows the policy — `SqlPreviewTests`
- [ ] add (Sample.Tests): outside Development, `/scry`, `/scry/introspect`, `/scry/sql`, and `/scry/_framework/x` are
  404 (`EnableGuard` default) — the sample sets `EnableGuard = _ => true`, so the default is never exercised
- [ ] add: `EnableSqlPreview` false with `EnableGuard` true — `/scry/sql` is 404 and introspection says `sqlPreview: false`
- [ ] add: asset paths with `..`, backslashes, and URL-encoded separators are 404 and never touch the file system
- [x] covered: F16's no-auto-run test (`UiSnapshotTests.ExplorerOpensASharedLinkWithoutRunningIt`)
- [ ] add: the SQL preview endpoint refuses a non-JSON `Content-Type` alongside F3, and its 500 body is the fixed text
- [x] covered: the host page is served under a `Content-Security-Policy` with its inline scripts allowed by hash —
  `UiSnapshotTests.ExplorerServesAContentSecurityPolicy`, `ExplorerAssetsCarryNoPolicy`; every browser test fails on a
  refusal the browser logs (`BrowserFixture.RefuseContentSecurityPolicyViolations`)


### Startup guardrails (`Schema.Build`, `MapScry`)

- [x] complex/entity mix-ups, inherited members from other assemblies, foreign enums, unreadable collection shapes,
  double opt-in, no-key cached policy, negative URL limit, unscoped caching, malformed collation —
  `GuardrailTests`, `LockstepTests`, `CachedPolicyTests.ATypeWithNoKeyIsRefusedAtStartup`, `UrlLimitTests`, `CollationTests`
- [ ] add: duplicate source names (two types named the same via `Name`) refuse to start
- [ ] add: a source name that is not a C# identifier refuses to start (`EnsureNameIsIdentifier`; the generator's SCRY003
  is tested, the server's twin is not)
- [ ] add: a policy type implementing `IReturnablePolicy<T>` for two `T`s, or for a type outside the hierarchy it is
  attached to, refuses to start (`RowPolicy.EntityType`, `Schema.ResolvePolicies`)
- [ ] add: `ProbePoliciedNavigations` runs every policy once with empty headers and no principal — pin that a policy
  which throws under those conditions is a startup failure naming it, and that clearing the option skips it
  (`NavigationPolicyTests.Probe*` cover translation failures, not construction failures)


### Out of scope, recorded for completeness

- The client's reading of a hostile server's response (unbounded payloads, multipart header limits) is outside the
  threat model per `docs/security.md`; `BufferedReadTests`, `BinaryConverterTests`, and `NdjsonReaderTests` pin what
  the client does bound.
- The source generator reads a trusted DLL at build time; a malformed DLL fails the build rather than the server.
