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
- The **[schema stamp](wire-format.md#schema-stamp)** covers the *model* — the allow-listed surface a client was generated against. It rides on every request and every response.

The wire version is a hard compatibility gate. The schema stamp is softer: a mismatch is not an error, but it is a signal a deployed client can use to notice it has drifted from the server. This page covers the client-side API for that.


## Detecting a stale client

A generated client is bound to the model surface it was generated against. That is fine while the two move together, but a **deployed** client can outlive a server redeploy — most obviously a Blazor WASM app the browser has cached and the user has left open in a tab. If the model has since changed incompatibly, the first symptom is otherwise a query that starts returning `400`.

Every response carries the server's [schema stamp](wire-format.md#schema-stamp), so the client can notice the drift *before* anything breaks. `ScryClient` exposes it three ways:

| Member | Use |
| --- | --- |
| `ServerSchemaStamp` | The stamp from the most recent response, or null before the first. |
| `SchemaStale` | True once that stamp differs from the client's own. Poll it wherever convenient. |
| `SchemaStaleDetected` | Raised the first time drift is seen. This is the one to handle to drive a prompt. |

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
<sup><a href='/samples/Sample.Client/StaleBanner.razor#L5-L13' title='Snippet source file'>snippet source</a> | <a href='#snippet-staleBannerMarkup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Prefer prompting over reloading automatically. A forced reload discards whatever the user was in the middle of, and their current client still works — there is no reason to interrupt them mid-task for a change that has not broken anything yet.


## When the break arrives

Eventually a drifted client's query does fail — a member it still references was removed, or a value it holds no longer parses. Those failures identify themselves:

- The server marks every rejection (and execution failure) from a mismatched stamp with `"staleClient": true` on the [error body](server.md#error-handling), keeping the plain 400/500 shape for a client that is plain wrong.
- `ScryClient` surfaces such failures as **`ScryStaleClientException`** — the same exception thrown client-side when a result carries an enum value name the generated model does not have. One catch covers every failure whose remedy is a newer client.

The two channels are complementary, and both fire on a failed query — the stamp header rides on rejections too, so `SchemaStaleDetected` has already been raised by the time the exception surfaces. An app with the banner above therefore needs no extra handling: the failed query throws, and the reload prompt is already on screen explaining why. Handle the exception where a better experience is possible — retrying the interrupted operation after the reload, say — not because the signal would otherwise be lost.

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
<sup><a href='/samples/Sample.Client/Pages/Index.razor.cs#L71-L80' title='Snippet source file'>snippet source</a> | <a href='#snippet-handleStaleClient' title='Start of snippet'>anchor</a></sup>
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
