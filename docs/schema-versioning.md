# Schema versioning

Every client/server API has to decide how much of its past it will keep working. That decision is a spectrum, and a system's position on it trades one set of costs for another. This page places Scry on that spectrum, then documents the mechanics.


## The versioning spectrum

At one end is **no versioning**: the server exposes exactly one surface, with no attempt to keep older clients working — so a client breaks the moment a server change is *incompatible* with what it uses. At the other is **never break**: every surface the server has ever shipped keeps working forever. Neither end is free — one pushes the cost onto *deployments*, the other onto *maintenance* — and most real systems sit somewhere between.

Five representative positions, from most willing to break to least:

- **No versioning** — one surface, no compatibility promises. A drifted client keeps working right up until the server ships a change it can't tolerate, and then gets a hard error with no warning. Cheapest to build, most disruptive when it does break.
- **Drift detection** — still one surface, but every response tells the client the server's current schema, so a stale client can *notice* it has drifted and update itself (reload, re-fetch, prompt) instead of failing a query. This is where Scry sits, via the [schema stamp](#the-two-version-axes).
- **Additive-only evolution** — a standing promise never to remove or change existing surface, only add. Old clients keep working on the subset they know. Nothing breaks, but the surface can only grow.
- **Parallel versions** — `v1`, `v2`, … served side by side, each a maintained contract, with clients migrating on their own schedule inside a deprecation window.
- **Never break** — perpetual back-compat. Every version is supported indefinitely; nothing a client has ever relied on is allowed to change.


### The impacts

The positions differ along a handful of axes worth naming explicitly:

- **Deployment risk** — the chance that shipping a server change breaks a client that is already live (or cached in a browser tab).
- **Server change freedom** — how freely server surface can be changed, renamed, or removed without ceremony. High freedom means a fast cadence; low freedom means every change is gated by compatibility.
- **Technical debt** — the compatibility shims, dead columns, deprecated fields, and parallel code paths carried forward to keep old clients working.
- **UI ↔ business-model lag** — how far a running UI is allowed to drift from the *current* model and business rules. Strong back-compat is double-edged: it lets an old client keep running, which also means users can keep seeing a stale shape of the business long after it changed.
- **Developer effort** — the ongoing engineering cost of the strategy, separate from debt: discipline, tests across versions, migration tooling.
- **Failure mode** — what a mismatch *feels like*: a hard error a user hits, a self-healing prompt, or nothing at all.


### Comparison

| | No versioning | Drift detection *(Scry)* | Additive-only | Parallel versions | Never break |
| --- | --- | --- | --- | --- | --- |
| **Deployment risk** | High — any incompatible change breaks live clients | Medium — breaking change still breaks in-flight queries, but the client self-heals; additive is safe | Low — old clients keep working | Low — old versions keep serving | Lowest — nothing ever breaks |
| **Server change freedom** | Highest — change anything, anytime | High — same freedom, with a safety net | Medium — add freely, never remove or change | Medium — new version anytime, but each is a commitment | Lowest — every change gated by compat forever |
| **Technical debt** | Lowest — one surface, no shims | Low — one surface | Growing — deprecated surface accumulates and can't be cleaned | High — multiple live code paths and migration machinery | Highest — everything supported forever |
| **UI ↔ business-model lag** | Uncontrolled — a lagging UI runs silently until a breaking change forces it out | Minimal — stale clients are detected and prompted promptly | Can be large — old clients linger indefinitely | Bounded by the deprecation window | Unbounded — ancient clients live forever; the model ossifies |
| **Developer effort** | Low ongoing, high per-deploy coordination | Low — the mechanism is built in | Medium — discipline to stay additive | High — build, test, and support N versions | Highest |
| **Failure mode** | Hard error the user hits first | Early signal before the break; graceful self-update | Silent — nothing breaks | Silent within the window, then a planned cutover | Silent |

No column is "correct" — the table is a map of what each position optimizes for. A tightly coupled internal app with lockstep deploys is well served by **no versioning**; a public API with thousands of uncontrolled consumers is pushed toward **parallel versions** or **never break** and pays for it in debt and lag.


### Where Scry sits

Scry's client is **generated and type-safe** against a specific model surface, so the query schema is deliberately *not* versioned in parallel — there is one surface at a time, and keeping several live would defeat the point. Instead Scry takes the **drift-detection** position: the wire *format* is versioned and fails closed (below), while the *model* surface is unversioned but stamped, so a deployed client detects drift early and updates itself rather than discovering the problem as a failed query. That keeps deployment cadence high and technical debt low, and accepts that a breaking model change requires clients to regenerate — which the stamp makes a graceful reload rather than a broken page.

Renames are the one breaking change with a built-in soft landing. `[PreviousNames]` ([Annotations](annotations.md#renaming)) keeps the server accepting the name a source, member, or enum value was previously exposed under, so a deployed client keeps querying while it picks up a regenerated one. Previous names stay out of the stamp, so the rename still registers as drift — they buy a migration window, not silence — and they are meant to be pruned once clients have refreshed, which is what keeps this short of the *additive-only* column above.

Renamed **enum values in results** take one extra step: the payload serializes the current name (a value cannot carry two names the way a response key can), so a drifted client's response also carries an [alias table](wire-format.md#response) mapping current names to previous ones, which the client's reader uses to resolve a name it does not know back to the one it was generated with. A name that still cannot be resolved — renamed without a `[PreviousNames]` entry, or removed — surfaces as a directed `ScryStaleClientException` rather than an unexplained parse failure.


## The two version axes

A Scry deployment has two independent version axes, both documented in [Wire format](wire-format.md):

- The **[wire version](wire-format.md#versioning)** covers the *format* — the shape of the request and response. The server rejects a request whose version is newer than its own.
- The **[schema stamp](wire-format.md#schema-stamp)** covers the *model* — the allow-listed surface a client was generated against. It is carried on every request and every response.

The wire version is a hard compatibility gate. The schema stamp is softer: a mismatch is not an error, but it is a signal a deployed client can use to notice it has drifted from the server. This page covers the client-side API for that.

What moves the stamp is what changes the surface a client was built against, or what a client is allowed to do with it. Adding, removing, renaming, or retyping an exposed member moves it, and so does [`[Sensitive]`](annotations.md#sensitive) — a client generated before that marking will keep asking in URLs and start being refused, and the stamp moving is what turns the refusal into a reported staleness rather than a mystery. [`[Obsolete]`](annotations.md#obsolete) does not move it: an obsolete member is still allowed, still validated, still executed, so hashing it would report every deployed client as stale over a note. Neither does the [URL budget](server.md#options), which describes the network in front of the server rather than the model.


## Detecting a stale client

A generated client is bound to the model surface it was generated against. That is fine while the two move together, but a **deployed** client can outlive a server redeploy — most obviously a Blazor WASM app the browser has cached and the user has left open in a tab. If the model has since changed incompatibly, the first symptom is otherwise a query that starts returning `400`.

Every response carries the server's [schema stamp](wire-format.md#schema-stamp), so the client can notice the drift *before* anything breaks. `ScryClient` exposes it three ways:

| Member | Use |
| --- | --- |
| `ServerSchemaStamp` | The stamp from the most recent response, or null before the first. |
| `SchemaStale` | True once that stamp differs from the client's own. Poll it wherever convenient. |
| `SchemaStaleDetected` | Raised the first time drift is seen. This is the one to handle to drive a prompt. |

The stamp arrives in the [response body](wire-format.md#response), so this works over **any** transport — SignalR, gRPC, or an in-process `ScryProcessor` — not only HTTP. The HTTP transport reads it from the `Scry-Schema-Stamp` header as well, which additionally covers error responses, where there is no body to read it from.

The event carries a `SchemaDrift` with both stamps (`ClientStamp`, `ServerStamp`), and is raised at most once per `ScryClient` — a chatty app does not re-prompt on every query.

Drift is **not** an error. The query that revealed it has already succeeded, and an additive model change — a new source, a new member — leaves an older client working indefinitely. Treat the signal as "a newer client exists", not "this client is broken".

The [sample](sample.md) handles it in a banner component rendered above the router, which subscribes on initialize and offers a reload:

<!-- snippet: detectSchemaDrift -->
<a id='snippet-detectSchemaDrift'></a>
```cs
protected override void OnInitialized() =>
    Client.SchemaStaleDetected += OnSchemaStale;

// The server has been redeployed with a different query surface, so a newer client exists. The
// app is still working — queries against the old surface keep succeeding — so show a prompt
// rather than reloading out from under whatever the user is doing.
void OnSchemaStale(SchemaDrift value)
{
    drift = value;
    InvokeAsync(StateHasChanged);
}

// Bypasses the browser cache, so the reload fetches the newly published client.
void Reload() =>
    Navigation.Refresh(forceReload: true);
```
<sup><a href='/samples/Sample.Client/StaleBanner.razor.cs#L12-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-detectSchemaDrift' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The prompt itself is ordinary markup — the banner renders only once drift has been seen:

<!-- snippet: staleBannerMarkup -->
<a id='snippet-staleBannerMarkup'></a>
```razor
@if (drift is not null)
{
    <div class="stale" role="alert">
        <span>A newer version of this app is available.</span>
        <button @onclick="Reload">Reload</button>
    </div>
}
```
<sup><a href='/samples/Sample.Client/StaleBanner.razor#L4-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-staleBannerMarkup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Prefer prompting over reloading automatically. A forced reload discards whatever the user was in the middle of, and their current client still works — there is no reason to interrupt them mid-task for a change that has not broken anything yet.


## When the break arrives

Eventually a drifted client's query does fail — a member it still references was removed, or a result no longer fits the shape it was generated for. Those failures identify themselves, and all of them arrive as one exception, **`ScryStaleClientException`**:

| Failure | Attributed by |
| --- | --- |
| The server rejects the query (`400`) | `"staleClient": true` on the [error body](server.md#error-handling) |
| The query faults during execution (`500`) | the same marker — a drifted client's query can still fault in ways neither validation nor rebinding can name |
| A result carries an enum value name the generated model lacks | the client's reader, after the [alias table](annotations.md#the-response-side) fails to resolve it |
| A result cannot be read at all — a widened numeric that now overflows, a member that became nullable | the client, when the stamp already shows it drifted |

The server keeps the plain 400/500 shape for a client that is plain wrong, and the client keeps the raw parse failure when the stamps agree — a current client failing to read a current payload is a bug, not drift, and must stay loud. Leniency applies only where the stamp *proves* the client is behind.

One catch therefore covers every failure whose remedy is a newer client. The original exception is preserved as `InnerException` where there was one.

This channel and the soft signal above are complementary, and both fire on the same failed query — the stamp header is present on rejections too, and is recorded before the payload is read. So `SchemaStaleDetected` has always been raised by the time the exception surfaces (which is also what lets the client classify an unreadable payload in the first place). An app with the banner above therefore needs no extra handling: the failed query throws, and the reload prompt is already on screen explaining why. Handle the exception where a better experience is possible — retrying the interrupted operation after the reload, say — not because the signal would otherwise be lost.

The [sample](sample.md)'s Index page distinguishes the stale failure from an application error where its queries run:

<!-- snippet: handleStaleClient -->
<a id='snippet-handleStaleClient'></a>
```cs
// The query failed because this deployed app was generated against a model surface the server
// no longer has. SchemaStaleDetected has already fired on the same response, so the reload
// banner is showing; render a directed placeholder for the data that could not load, rather
// than presenting the failure as an application error.
catch (ScryStaleClientException)
{
    stale = true;
}
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L75-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-handleStaleClient' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The stale branch renders a directed placeholder in place of the data — the fix is a reload, and the banner offering one is already visible above:

<!-- snippet: staleDataMarkup -->
<a id='snippet-staleDataMarkup'></a>
```razor
@if (stale)
{
    <p class="stale" role="alert">
        This page needs a newer version of the app. Use the reload prompt above to update.
    </p>
}
```
<sup><a href='/samples/Sample.Client/Pages/Index.razor#L18-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-staleDataMarkup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The generic `catch` stays: a failure without the stale marker is an ordinary error and should keep looking like one. Order matters — `ScryStaleClientException` first, since the general handler would otherwise swallow it.


## What is not handled

The table above is the set of changes a deployed client survives or is told about. This is its mirror — the changes that are **not** bridged, and why each is excluded by design rather than by omission. None is a bug to be fixed later; each is a place where the alternative was worse.

Each entry below leads with the **direction** the change bites from: filtering a query (the request), or materializing rows (the response). Several changes reach both, and the two halves do not always fare alike — a representation change is caught coming back and missed going out.


### Adding an enum value that reaches results

**Request:** unaffected. **Response:** `ScryStaleClientException` on the row carrying the new value.

The [alias table](annotations.md#the-response-side) maps a renamed value to a name the client already has. A genuinely new value has no old name to map from, so there is nothing to translate. Additive on the server, breaking for old clients — the one asymmetry in the additive story.


### Changing a scalar's representation

**Both directions:** sometimes a clean failure, sometimes nothing at all — and which one arrives turns on the *value*, not on the change. This is the least reliable signal in the whole story, and the two halves fail by different rules, so they are worth taking separately.


#### The request half

A literal is sent on the wire as text plus a loose [`ClrTypeTag`](wire-format.md#const). At a comparison site the server **ignores the tag** and parses that text into whatever type its own schema now gives the member — CLR types come from the schema, never from the wire. So the question is never "did the type change", but "does this text still parse, and is this operator still defined for the result".

*Loosening* to `string` — from a number, a `bool`, an enum, anything — always parses, because reading text as text cannot fail:

| A client generated against `int Age` wrote | The server builds | Result |
| --- | --- | --- |
| `Age == 30` | `Age == "30"` | **Silent.** Executes and returns whatever matches the text. |
| `Age != 30` | `Age != "30"` | **Silent.** |
| `Age > 30` | — | Rejected — `>` is not defined for two strings. |

Ordering is the accident that rescues this case: .NET defines no relational operator on `string`, so the expression cannot be built and the query is rejected before it runs. Equality *is* defined, so it goes straight through. **An equality filter against a member retyped to `string` is the one case with no signal on either half of the round trip.**

*Tightening* away from `string` is caught only when the text does not parse in the new type:

| A client generated against `string` wrote | The server does | Result |
| --- | --- | --- |
| `Id == "1"` | parses `1` | **Silent** — and, as it happens, correct. |
| `Status == "FullTime"` | parses the enum value | **Silent** — and correct. |
| `Status == "Alice"` | enum parse fails | Rejected (`400`) — `'Alice' is not a value of enum 'Status'.` |
| `Id == "Alice"` | `int.Parse` fails | Rejected (`400`) — `'Alice' is not a valid Int32 value.` |

Parsing happens while the expression is being rebound, after validation has already passed — but a value that does not parse is still reported as a rejected query, never as a server fault. An enum or `char` names what the text failed to be; every other scalar names the member's type. For a drifted client the rejection is attributed to the stamp and reaches the client as `ScryStaleClientException`, so one catch covers it.


#### The response half

Materializing rows is governed by **JSON's token kinds**, not by CLR types. A change that crosses from one token kind to another cannot be read; one that stays inside a kind is invisible until a value stops fitting:

| Retype | Token kinds | Result |
| --- | --- | --- |
| `int` → `string`, or `string` → `int` | number ↔ string | Caught |
| `bool` → `string`, or `string` → `bool` | boolean ↔ string | Caught |
| enum → `string`, or `string` → enum | string → string | **Silent** — enum values serialize as their names, which are strings either way |
| `Guid` or `DateTime` → `string` | string → string | **Silent** |
| `int` → `long` | number → number | **Silent** until a value exceeds `int` |

So the response half is *not* the safety net it looks like. It catches a retype only when the new type serializes to a different JSON token — and the string family is wide enough that enum, `Guid` and `DateTime` all pass for text. When it does catch one, the failure is a bare `JsonException`, upgraded to `ScryStaleClientException` only [where the stamp already proves the client is behind](#when-the-break-arrives).


#### Why it is not bridged

Catching the request half would mean type-checking `ConstNode.Tag` against the member, but the tags are deliberately loose — `uint` and `ulong` have no tag of their own and use the `String` tag, as do `char`, while `short` and `byte` use `Int32` — so a strict check would reject legitimate traffic from a perfectly current client. Loosening it to "compatible" re-admits exactly the number-versus-text case worth catching, since `String` is the fallback bucket. There is no rule that separates the two.

Treat a representation change as requiring a coordinated deploy, or rename the member alongside it ([below](#the-common-shape)).


### Tightening a limit

**Request:** the plain `400` the limit produces, with no drift signal.

`MaxPageSize`, `MaxNavigationDepth` and the rest are [server options](server.md#options), not schema, so they are correctly absent from the stamp. A reload would not help: the client's own code asks for the page size, so the remedy is an app change. Flagging it as drift would tell the user to do something that cannot fix it.


### Reusing a retired name for something else

**Both directions:** whatever now answers to that name — silently. A reused source or member misdirects a filter; a reused enum value materializes a row as the wrong member.

Every other mistake here fails loudly. This one cannot: once an entry is pruned, nothing records that the name ever meant something else, so no startup check can catch it. See [pruning](annotations.md#renaming).


### Removing a source, member, or enum value

**Both directions:** `ScryStaleClientException` — the filter is rejected, or the row cannot be read.

Not bridgeable *by definition* — the data is gone. It appears among the [handled failures](#when-the-break-arrives) because it is detected and attributed, not because the client keeps working.


### The common shape

Most of these share a shape worth naming: the *meaning* moved while the *name* stayed. Scry's compatibility machinery is a name-mapping layer — it can say "this used to be called that", and nothing more. A change that keeps a name but alters what it denotes is outside what any such layer can express, in either direction, which is why the answer there is a coordinated deploy rather than a wider mechanism. Tightened limits are the odd one out: not a name problem at all, but a bound on what the surface will serve.

That framing also supplies the workaround where a coordinated deploy is not on offer: if the meaning has to move, move the name with it. Retyping `Age` from `int` to `string` **and** renaming it — with no `PreviousNames` entry, since the old name no longer denotes what it did — converts an unbridged change into a bridged one. The member the deployed client filters on is gone, so the query is rejected, the rejection is attributed to the stamp, and the client gets a `ScryStaleClientException` pointing at the reload prompt rather than silently comparing numbers as text. This does not keep the old client working; it makes it fail where it would otherwise have been quietly wrong, at the cost of a rename the model may not otherwise want.

For anything on this list, the [stamp](#the-two-version-axes) still changes, so `SchemaStaleDetected` still fires and the reload prompt still appears — the difference is that queries break before the user acts on it, rather than continuing to work while they decide.
