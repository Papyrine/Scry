# Caching and 304 Not Modified

Scry caches nothing. Every query is executed, and every response is written in full — which is the right default, and wasteful whenever a client asks the same question twice against a database that has not changed in between.

[304 Not Modified](https://www.keycdn.com/support/304-not-modified) is the standard answer to that: the server hands out an `ETag` with a response, the client sends it back as `If-None-Match` next time, and a server that can cheaply prove nothing has changed replies with a status and no body. The hard part is the proof, and [Delta](https://github.com/SimonCropp/Delta) is a small library that supplies it — an `ETag` derived from the database's own change tracking, so "has anything changed" is one cheap read rather than a re-execution.

The ETag itself is not built into Scry and is not required. The [sample](sample.md) wires it up end to end — one middleware on the server, one `DelegatingHandler` on the client — and this page is that wiring explained. What *is* built in is the half that makes any of it reachable: a query short enough to fit in a URL is asked with `GET`, so the caches that already exist can answer it.


## Why a query is a URL

A cache — the browser's, a proxy's, a CDN's — decides what it can store from the method and the URL, and it decides before it looks at anything else. A `POST` is uncacheable to all of them, and its body is not part of any cache key, so a query sent as one is invisible to every cache between the client and the server no matter what headers it carries.

So a query that fits is sent as `GET {endpoint}?q={encoded}`, where `q` is the same serialized `QueryRequest` base64url-encoded ([`QueryUrl`](wire-format.md#the-url-form)). The URL identifies the response, which is what a cache needs, and `304` becomes what it is everywhere else on the web rather than something both ends have to hand-implement.

Three consequences worth stating plainly:

**The request travels in the URL, not in content on the GET.** A body would carry any query at any size, and it cannot be used. A browser refuses to send one — the Fetch standard forbids content on `GET`, which rules it out for a WASM client and for the explorer. And an intermediary is permitted to drop the content of a `GET`: what reaches the server is then still a well-formed request — same method, same URL — carrying nothing to execute, so the server answers 400 and the client that sent a complete request cannot tell that from a rejection it caused itself. The failure is silent, depends on infrastructure the client cannot see, and does not reproduce locally. A URL survives every hop by construction.

**A URL has a ceiling, so `POST` stays mapped.** 8 KB is the usual server and proxy limit on a whole request line, and what exceeds it is rejected by whichever hop is strictest, as a 414 or a 400 depending on the deployment. `QueryUrl.MaxLength` is set well below that; a query over it is sent as a body exactly as before, with no cache involvement. An `IN` list is the easiest way to get there — a few hundred ids is enough.

**A URL is logged.** Everything in the request, including the constants a filter compares against, lands in the access log of every hop and in the `Referer` of whatever the page does next. A query whose constants are sensitive on their own — an account number, a person's id — is one to keep on `POST` regardless of length.

Beyond that, the ETag is the app's own to wire up. What Delta supplies is the part that is actually hard — `GetLastTimeStamp`, one cheap read of the database's change marker, in whatever form the provider underneath offers it (the transaction log's end position on SQL Server, `pg_last_committed_xact` on PostgreSQL). Everything above that is a dozen lines of plumbing. Delta's own `UseDelta` covers an app's ordinary GET traffic — the host page, static assets, conventional endpoints — with one line and no client-side work.

| Package | Use |
| --- | --- |
| [`Delta.EF`](https://nuget.org/packages/Delta.EF/) | `GetLastTimeStamp` on a `DbContext`. What the sample references. |
| [`Delta`](https://nuget.org/packages/Delta/) | The same over a raw `DbConnection`, plus `UseDelta` for the app's GET traffic. |
| [`Delta.SqlServer`](https://nuget.org/packages/Delta.SqlServer/) | Helpers for enabling and inspecting SQL Server change tracking. |


## What identifies a response

The ETag has to change whenever the bytes it stands for would change. Three things decide those bytes:

| Part | Changes when | Read from |
| --- | --- | --- |
| Schema stamp | the queryable surface is redeployed | `ScryProcessor.SchemaStamp` |
| Database timestamp | anything is written | Delta's `GetLastTimeStamp` |
| Query fingerprint | a different question is asked | a hash of the `q` parameter |

The fingerprint costs nothing to obtain: the encoded request in the URL *is* the query, so hashing it identifies the query exactly. It is hashed only for size — `q` runs to thousands of characters where an ETag wants a handful. Being a hash of the encoded **bytes** means two spellings of the same query miss rather than collide, which is the safe direction for a cache key: a miss costs a round trip, a collision would cost correctness.

Nothing about it comes from the client's own account of what it sent. There is no header asserting a query hash and nothing that reads one — a client cannot describe its request as something other than what its URL says, because the URL is what the server hashed.

Delta's own ETag opens with the entry assembly's last write time, so any redeployment invalidates every entry. The schema stamp is the narrower version of that idea — narrower because a redeployed binary that left the queryable surface alone keeps its caches warm. It also means a client whose model has drifted can never be answered 304: the stamp in its ETag is the old one, so the comparison fails and it gets a full response carrying the server's current stamp, which is what [drift detection](schema-versioning.md) reads.


## The server half

<!-- snippet: queryEtagMiddleware -->
<a id='snippet-queryEtagMiddleware'></a>
```cs
public static IApplicationBuilder UseQueryEtag<TContext>(
    this IApplicationBuilder builder,
    string path,
    Func<HttpContext, string?>? suffix = null)
    where TContext : DbContext =>
    builder.Use(
        async (context, next) =>
        {
            var request = context.Request;

            // No URL-borne query, no cache key. A request without one — a query too long for a
            // URL, a raw one written by hand, a health probe routed through the same path — is
            // answered exactly as it was before this middleware existed.
            if (!request.Path.StartsWithSegments(path) ||
                Fingerprint(request) is not { } fingerprint)
            {
                await next();
                return;
            }

            var db = context.RequestServices.GetRequiredService<TContext>();
            var stamp = context.RequestServices.GetRequiredService<ScryProcessor>().SchemaStamp;

            // Delta, doing the part that is actually hard: one cheap read of the database's own
            // change marker, whatever the provider underneath spells it as.
            var timeStamp = await db.GetLastTimeStamp(context.RequestAborted);

            var etag = Etag(stamp, timeStamp, fingerprint, suffix?.Invoke(context));
            context.Response.Headers.ETag = etag;

            if (request.Headers.IfNoneMatch != etag)
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status304NotModified;

            // Delta's own: the client may reuse what it holds, but has to ask again next time
            // rather than assume an expiry it was never given.
            context.Response.NoCache();

            // And `private` with it, because a client updates the headers of the response it kept
            // with the ones a 304 carries. Sending `no-cache` alone would strip `private` from the
            // stored copy of a response that was only ever meant for this caller.
            context.Response.Headers.CacheControl = "private, no-cache";
        });
```
<sup><a href='/samples/Sample.Server/QueryEtag.cs#L43-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryEtagMiddleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Registered ahead of the endpoint, against the pattern `MapScry` was given — so it covers the query endpoint and the stream, batch, and attachment endpoints below it:

<!-- snippet: sampleQueryEtag -->
<a id='snippet-sampleQueryEtag'></a>
```cs
app.UseQueryEtag<SampleContext>("/api/query");
```
<sup><a href='/samples/Sample.Server/Program.cs#L57-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sampleQueryEtag' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A request without a fingerprint is answered exactly as it was before the middleware existed: no ETag, no short circuit. That keeps hand-written requests, health probes, and anything else routed through the same path out of it.


## The client half

In a browser, most of this half already exists. A query asked as a URL is an ordinary cacheable `GET`, so the browser stores the response, sends `If-None-Match` on its own next time, and turns the `304` back into the rows it already holds — none of which the app sees. That is the whole reason the request is a URL.

What the handler below adds is the same behaviour where that cache does not exist: a console app, a service, a test host. It matters here because the sample's own tests run outside a browser, and because without it `ScryClient` treats a bare 304 as what it is at that layer — a response with no rows in it, surfaced as a `ScryRequestException` with status 304.

It is a `DelegatingHandler` below the client, which re-asks with `If-None-Match` and rebuilds the response a 304 stands for:

<!-- snippet: clientCacheHandler -->
<a id='snippet-clientCacheHandler'></a>
```cs
public sealed class QueryCacheHandler(QueryCache cache) :
    DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Key(request) is not { } key)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var cached = cache.Get(key);
        if (cached is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified &&
            cached is not null)
        {
            cache.RecordHit();
            return Replay(request, response, cached);
        }

        cache.RecordMiss();

        // Nothing to store: either the server is not offering ETags, or this is a response that
        // cannot be replayed from bytes held in memory.
        if (response.Headers.ETag?.ToString() is not { } etag ||
            response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            return response;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        cache.Store(key, new(etag, body, "application/json", Stamp(response)));

        // The content has been read to the end, so the response is handed back over the bytes rather
        // than over the stream they came out of.
        return WithBody(request, response, body, "application/json");
    }
```
<sup><a href='/samples/Sample.Client/QueryCacheHandler.cs#L15-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientCacheHandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Registered into the named client's pipeline, with the store held apart from it — the factory rotates handlers every couple of minutes, and a cache that rotated with them would forget everything:

<!-- snippet: clientCacheRegistration -->
<a id='snippet-clientCacheRegistration'></a>
```cs
builder.Services.AddSingleton<QueryCache>();
builder.Services.AddTransient<QueryCacheHandler>();
builder.Services
    .AddHttpClient("scry")
    .AddHttpMessageHandler<QueryCacheHandler>();
```
<sup><a href='/samples/Sample.Client/Program.cs#L28-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-clientCacheRegistration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Above the handler nothing changes: the same `ScryClient`, the same generated models, the same rows.


## The exchange

Two identical queries, recorded at the socket. Both are `GET`s carrying the query in `q` — shown decoded, since what travels is base64url. The first is answered in full and carries an `ETag`; the second sends it back and is answered with a status and nothing else:

<!-- snippet: ConditionalQueryTests.ConditionalExchange.verified.txt -->
<a id='snippet-ConditionalQueryTests.ConditionalExchange.verified.txt'></a>
```txt
[
  {
    RequestUri: {
      Path: http://localhost/api/query,
      Query: {
        q: {"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"binary","op":"AndAlso","left":{"$type":"member","path":["Active"]},"right":{"$type":"binary","op":"Equal","left":{"$type":"member","path":["Department","Name"]},"right":{"$type":"const","value":"Engineering","tag":"String"}}}},{"$type":"orderBy","key":{"$type":"member","path":["Name"]},"descending":false},{"$type":"select","projection":{"members":[{"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Name"]}}}]}}],"stamp":"{scrubbed stamp}"}
      }
    },
    RequestMethod: GET,
    ResponseStatus: OK 200,
    ResponseHeaders: {
      Cache-Control: no-cache, private,
      ETag: {Scrubbed},
      Scry-Schema-Stamp: {Scrubbed}
    },
    ResponseContent: {"version":2,"kind":"List","payload":[{"name":"Aaron"},{"name":"Alice"}],"stamp":"{scrubbed stamp}"}
  },
  {
    RequestUri: {
      Path: http://localhost/api/query,
      Query: {
        q: {"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"binary","op":"AndAlso","left":{"$type":"member","path":["Active"]},"right":{"$type":"binary","op":"Equal","left":{"$type":"member","path":["Department","Name"]},"right":{"$type":"const","value":"Engineering","tag":"String"}}}},{"$type":"orderBy","key":{"$type":"member","path":["Name"]},"descending":false},{"$type":"select","projection":{"members":[{"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Name"]}}}]}}],"stamp":"{scrubbed stamp}"}
      }
    },
    RequestMethod: GET,
    RequestHeaders: {
      If-None-Match: {Scrubbed}
    },
    ResponseStatus: NotModified 304,
    ResponseHeaders: {
      Cache-Control: no-cache, private,
      ETag: {Scrubbed}
    }
  }
]
```
<sup><a href='/samples/Sample.Tests/ConditionalQueryTests.ConditionalExchange.verified.txt#L1-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-ConditionalQueryTests.ConditionalExchange.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The ETag values are scrubbed from that snapshot because they carry the database's log position. That the two match is what the 304 proves.

Note what the 304 does **not** carry: `Scry-Schema-Stamp`, since the response was never built by Scry. The handler replays the stamp it cached, and drift would have produced a full response anyway — the stamp is part of the ETag.


## What it costs

Every request now reads the database timestamp before doing anything else. That read is cheap — it is a lookup, not a scan — but it is not free, and it happens whether or not the request turns out to be a hit. The trade is one lookup against one query execution plus one response serialization, which pays off in almost any read-heavy app and does not pay off if the data changes on every request.

Delta can remove even that read: its `UseDelta` accepts `Cache-Control` request directives (`max-age`, `max-stale`) and will reuse a recently-read timestamp rather than going back to the database. The middleware here does not implement that; `GetLastTimeStamp` is called every time.


## The sharp edges

**A write is not visible instantly.** On SQL Server the log position Delta reads trails a committed transaction — a couple of hundred milliseconds on LocalDB. Inside that window a client that has written can still be told its cached copy is current. That is fine for a dashboard and wrong for read-after-write, so a client that writes should either bypass the cache afterwards or not use one. It is the same assumption Delta states outright: this approach suits data whose update frequency is low relative to reads.

**Anything a response varies by must be in the key.** A [row policy](policies.md) that scopes rows to a tenant, or an [attachment policy](attachments.md) that answers per principal, makes two identical queries produce different bytes for different callers. The query fingerprint cannot see that. Pass it as the `suffix` — the tenant id, the user id — or a client whose identity changes mid-session can be handed a 304 for rows the new identity was never shown. For the same reason, the client's store has to be cleared on sign-in and sign-out.

**A query in a body is never answered conditionally.** The middleware keys on the URL, so a query too long to be one gets no ETag and no 304 — it is answered exactly as it would be with none of this wired up. That is not a gap so much as the same fact from the other side: what makes a response identifiable to a cache is the URL, and a body-borne query has none. An app that wants those cached too needs an identity of its own making, and should read the paragraph above before inventing one that the client supplies.

**A URL response is cacheable, so the server says who may keep it.** Every answer to a `GET` carries `Cache-Control: private, no-cache`, and both halves are load-bearing. `private` is what keeps a CDN or a shared proxy out of it: rows are shaped by [policies](policies.md) that read the request, so the same URL answers differently for two principals, and a shared cache keyed on the URL alone would hand one of them the other's rows. `no-cache` keeps a stored copy revalidating rather than expiring on a guess — without a directive, a browser is free to invent a freshness lifetime and serve stale rows without asking. An app whose rows genuinely do not vary by caller can widen this above the endpoint; nothing else should.

**Not everything is cacheable.** The handler keeps `application/json` responses only. A [streamed](querying.md#streaming-rows) result is meant to be read a row at a time and a [multipart](wire-format.md#binary-transfer) one carries binary parts beside its envelope; caching either means buffering it whole, so both pass through untouched. They still get an ETag from the server — a client that wants to cache them needs a strategy of its own.

**It is a cache, so it needs a bound.** The sample's is an unbounded dictionary, which suits a page that asks the same handful of queries. A real one wants an LRU, or a cap on the bytes held.


## The simpler options

Not every app needs this. Worth considering first:

- **Nothing.** A query that runs in single-digit milliseconds against a warm database does not need a caching layer, and this one costs a timestamp read per request plus two places where staleness can hide.
- **Delta's `UseDelta`, on the app's GET traffic.** The Blazor host page, static assets, and any conventional endpoints get the same treatment with one line and no client-side work, because the browser already implements the client half.
- **A shorter-lived client cache with no server involvement.** If "the last few seconds" is fresh enough, a client that reuses a response for a fixed window skips the round trip entirely rather than shortening it.
