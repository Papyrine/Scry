# Schema versioning

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
| `SchemaStaleDetected` | Raised the first time drift is seen. This is the one to handle if you want to prompt. |

The event hands you a `SchemaDrift` carrying both stamps (`ClientStamp`, `ServerStamp`), and is raised at most once per `ScryClient` — a chatty app does not re-prompt on every query.

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
