# Caching and 304 Not Modified

Scry caches nothing. Every query is executed, and every response is written in full — which is the right default, and wasteful whenever a client asks the same question twice against a database that has not changed in between.

[304 Not Modified](https://www.keycdn.com/support/304-not-modified) is the standard answer to that: the server hands out an `ETag` with a response, the client sends it back as `If-None-Match` next time, and a server that can cheaply prove nothing has changed replies with a status and no body. The hard part is the proof, and [Delta](https://github.com/SimonCropp/Delta) is a small library that supplies it — an `ETag` derived from the database's own change tracking, so "has anything changed" is one cheap read rather than a re-execution.

None of this is built into Scry, and none of it is required. The [sample](sample.md) wires it up end to end — one middleware on the server, one `DelegatingHandler` on the client — and this page is that wiring explained.


## Why Delta's own middleware is not enough here

Delta's `UseDelta` handles GET requests, where the URL identifies the response and the browser's own cache does the client half for free. Scry posts its query as a body: the path is the same for every query, so the URL identifies nothing, and no intermediary caches a POST response anyway.

So both halves are yours. What Delta supplies is the part that is actually hard — `GetLastTimeStamp`, one cheap read of the database's change marker, in whatever form the provider underneath offers it (the transaction log's end position on SQL Server, `pg_last_committed_xact` on PostgreSQL). Everything above that is a dozen lines of plumbing.

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
| Query fingerprint | a different question is asked | the `Scry-Query-Hash` request header |

The fingerprint is already there: `ScryClient` hashes the exact bytes of every request it sends and carries the result in `Scry-Query-Hash` — part of [the wire contract](wire-format.md#versioning). It is a hash of the **bytes**, so two spellings of the same query miss rather than collide, which is the safe direction for a cache key: a miss costs a round trip, a collision would cost correctness.

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

            // No fingerprint, no cache key. A request without one — a raw one written by hand, or
            // anything else routed through here — is answered exactly as it was before.
            if (!request.Path.StartsWithSegments(path) ||
                QueryFingerprint.TryRead(request.Headers[WireFormat.QueryHashHeader]) is not { } fingerprint)
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
        });
```
<sup><a href='/samples/Sample.Server/QueryEtag.cs#L42-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-queryEtagMiddleware' title='Start of snippet'>anchor</a></sup>
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

Without a client that understands them, ETags do nothing — and worse than nothing, since `ScryClient` treats a bare 304 as what it is at that layer: a response with no rows in it, surfaced as a `ScryRequestException` with status 304.

The fix is a `DelegatingHandler` below the client, which re-asks with `If-None-Match` and rebuilds the response a 304 stands for:

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

Two identical queries, recorded at the socket. The first is answered in full and carries an `ETag`; the second sends it back and is answered with a status and nothing else:

<!-- snippet: ConditionalQueryTests.ConditionalExchange.verified.txt -->
<a id='snippet-ConditionalQueryTests.ConditionalExchange.verified.txt'></a>
```txt
[
  {
    RequestUri: http://localhost/api/query,
    RequestMethod: POST,
    RequestContent: {"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"binary","op":"AndAlso","left":{"$type":"member","path":["Active"]},"right":{"$type":"binary","op":"Equal","left":{"$type":"member","path":["Department","Name"]},"right":{"$type":"const","value":"Engineering","tag":"String"}}}},{"$type":"orderBy","key":{"$type":"member","path":["Name"]},"descending":false},{"$type":"select","projection":{"members":[{"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Name"]}}}]}}],"stamp":"{scrubbed stamp}"},
    ResponseStatus: OK 200,
    ResponseHeaders: {
      ETag: {Scrubbed},
      Scry-Schema-Stamp: {Scrubbed}
    },
    ResponseContent: {"version":2,"kind":"List","payload":[{"name":"Aaron"},{"name":"Alice"}],"stamp":"{scrubbed stamp}"}
  },
  {
    RequestUri: http://localhost/api/query,
    RequestMethod: POST,
    RequestHeaders: {
      If-None-Match: {Scrubbed}
    },
    RequestContent: {"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"binary","op":"AndAlso","left":{"$type":"member","path":["Active"]},"right":{"$type":"binary","op":"Equal","left":{"$type":"member","path":["Department","Name"]},"right":{"$type":"const","value":"Engineering","tag":"String"}}}},{"$type":"orderBy","key":{"$type":"member","path":["Name"]},"descending":false},{"$type":"select","projection":{"members":[{"name":"Name","value":{"$type":"node","node":{"$type":"member","path":["Name"]}}}]}}],"stamp":"{scrubbed stamp}"},
    ResponseStatus: NotModified 304,
    ResponseHeaders: {
      Cache-Control: no-cache,
      ETag: {Scrubbed}
    }
  }
]
```
<sup><a href='/samples/Sample.Tests/ConditionalQueryTests.ConditionalExchange.verified.txt#L1-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-ConditionalQueryTests.ConditionalExchange.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The ETag values are scrubbed from that snapshot because they carry the database's log position. That the two match is what the 304 proves.

Note what the 304 does **not** carry: `Scry-Schema-Stamp`, since the response was never built by Scry. The handler replays the stamp it cached, and drift would have produced a full response anyway — the stamp is part of the ETag.


## What it costs

Every request now reads the database timestamp before doing anything else. That read is cheap — it is a lookup, not a scan — but it is not free, and it happens whether or not the request turns out to be a hit. The trade is one lookup against one query execution plus one response serialization, which pays off in almost any read-heavy app and does not pay off if the data changes on every request.

Delta can remove even that read: its `UseDelta` accepts `Cache-Control` request directives (`max-age`, `max-stale`) and will reuse a recently-read timestamp rather than going back to the database. The middleware here does not implement that; `GetLastTimeStamp` is called every time.


## The sharp edges

**A write is not visible instantly.** On SQL Server the log position Delta reads trails a committed transaction — a couple of hundred milliseconds on LocalDB. Inside that window a client that has written can still be told its cached copy is current. That is fine for a dashboard and wrong for read-after-write, so a client that writes should either bypass the cache afterwards or not use one. It is the same assumption Delta states outright: this approach suits data whose update frequency is low relative to reads.

**Anything a response varies by must be in the key.** A [row policy](policies.md) that scopes rows to a tenant, or an [attachment policy](attachments.md) that answers per principal, makes two identical queries produce different bytes for different callers. The query fingerprint cannot see that. Pass it as the `suffix` — the tenant id, the user id — or a client whose identity changes mid-session can be handed a 304 for rows the new identity was never shown. For the same reason, the client's store has to be cleared on sign-in and sign-out.

**The fingerprint comes from the client.** It is never trusted as more than a cache key. A client that sends a wrong one can only be told that its own cached response is still current — a lie it told itself. It cannot read another client's response, and it cannot widen what it is allowed to see, because the ETag is only ever compared against a value this server minted. Never key a *shared* cache on it.

**Not everything is cacheable.** The handler keeps `application/json` responses only. A [streamed](querying.md#streaming-rows) result is meant to be read a row at a time and a [multipart](wire-format.md#binary-transfer) one carries binary parts beside its envelope; caching either means buffering it whole, so both pass through untouched. They still get an ETag from the server — a client that wants to cache them needs a strategy of its own.

**It is a cache, so it needs a bound.** The sample's is an unbounded dictionary, which suits a page that asks the same handful of queries. A real one wants an LRU, or a cap on the bytes held.


## The simpler options

Not every app needs this. Worth considering first:

- **Nothing.** A query that runs in single-digit milliseconds against a warm database does not need a caching layer, and this one costs a timestamp read per request plus two places where staleness can hide.
- **Delta's `UseDelta`, on the app's GET traffic.** The Blazor host page, static assets, and any conventional endpoints get the same treatment with one line and no client-side work, because the browser already implements the client half.
- **A shorter-lived client cache with no server involvement.** If "the last few seconds" is fresh enough, a client that reuses a response for a fixed window skips the round trip entirely rather than shortening it.
