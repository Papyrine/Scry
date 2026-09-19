# Scry.Server.Delta

Conditional requests for [Scry](https://github.com/Papyrine/Scry), backed by [Delta](https://github.com/SimonCropp/Delta).

A query [asked as a URL](https://github.com/Papyrine/Scry/blob/main/docs/wire-format.md#the-url-form) can be answered `304 Not Modified` when nothing has changed since the caller last asked. The hard part of that is proving it, and Delta supplies the proof: one cheap read of the database's own change marker, rather than executing the query again.

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.UseDeltaFreshness<SampleContext>();
    });
```

That is the whole wiring. Everything above it — the `ETag`, the `If-None-Match` comparison, the `304`, the cache directives — is `Scry.Server`'s, and works with any freshness source through `ScryOptions.QueryFreshness`. This package supplies the one most apps want.

<!-- snippet: useDeltaFreshness -->
<a id='snippet-useDeltaFreshness'></a>
```cs
/// <summary>
/// Answers a repeated query with <c>304 Not Modified</c> while nothing has been written, by
/// reading <typeparamref name="TContext"/>'s own change marker through Delta's
/// <c>GetLastTimeStamp</c> — the transaction log's end position on SQL Server,
/// <c>pg_last_committed_xact</c> on PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// One read of a marker the database already maintains, in place of executing the query and
/// writing its rows. That trade pays in almost any read-heavy app and does not pay where the data
/// changes on every request, since the marker moves for a write to anything at all.
/// </para>
/// <para>
/// The marker trails a commit rather than moving with it — a couple of hundred milliseconds on
/// SQL Server — so inside that window a client that has just written can be told its copy is
/// still current. A client that needs read-after-write sends <c>Cache-Control: no-cache</c>,
/// which skips the comparison and re-executes.
/// </para>
/// <para>
/// Where any source carries a row or attachment policy, its rows depend on who asked, and
/// <see cref="ScryOptions.CacheScope"/> has to say what a cached response belongs to.
/// <c>MapScry</c> refuses to start otherwise.
/// </para>
/// </remarks>
public static ScryOptions UseDeltaFreshness<TContext>(this ScryOptions options)
    where TContext : DbContext
{
    options.QueryFreshness = async (context, cancel) =>
    {
        var data = context.RequestServices.GetRequiredService<TContext>();
        var timeStamp = await data.GetLastTimeStamp(cancel);

        // A marker that says nothing identifies nothing, so the request is answered in full rather
        // than with an ETag that has a hole where its freshness should be.
        if (timeStamp.Length == 0)
        {
            return null;
        }

        return timeStamp;
    };

    return options;
}
```
<sup><a href='/src/Scry.Server.Delta/ScryDeltaExtensions.cs#L9-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-useDeltaFreshness' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Where a source carries a [row policy](https://github.com/Papyrine/Scry/blob/main/docs/policies.md), its rows depend on who asked, so `ScryOptions.CacheScope` has to say what a cached response belongs to — `MapScry` refuses to start otherwise.


## Live queries

The same marker tells a [live query](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md) that something was written, whoever wrote it:

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxSubscriptions = 1000;
        _.UseDeltaChanges<SampleContext>();
    });
```

The marker moves for another node, another system, a bulk update and raw SQL alike, so the database is the backplane and a deployment of several nodes needs no other.

<!-- snippet: useDeltaChanges -->
<a id='snippet-useDeltaChanges'></a>
```cs
/// <summary>
/// Runs every live query again when anything is written to <typeparamref name="TContext"/>'s
/// database, by watching the same change marker <see cref="UseDeltaFreshness{TContext}"/> reads.
/// </summary>
/// <remarks>
/// <para>
/// The marker moves for every writer there is — another node, another system, a bulk update, raw
/// SQL — none of which has to know Scry exists. That makes the database the backplane: a
/// deployment of several nodes needs nothing else for a write on one to reach the live queries
/// held by the others.
/// </para>
/// <para>
/// What it cannot say is what changed, so every live query is run again rather than the ones that
/// read what was written. A run that finds its answer unchanged sends nothing, so the cost is
/// queries and not traffic — and it is paid only while a live query is open, since nothing probes
/// otherwise. Use it beside <see cref="ScryChangeInterceptor"/>, which does know what changed and
/// reports it at once: the interceptor makes this node's own writes fast and precise, and this
/// catches everything the interceptor cannot see.
/// </para>
/// <para>
/// The marker trails a commit by a couple of hundred milliseconds on SQL Server, and is asked
/// every <see cref="ScryOptions.ChangeProbeInterval"/>, so that is how far behind a write this
/// alone can be.
/// </para>
/// </remarks>
public static ScryOptions UseDeltaChanges<TContext>(this ScryOptions options)
    where TContext : DbContext
{
    options.ChangeProbe = async (services, cancel) =>
    {
        var data = services.GetRequiredService<TContext>();
        var timeStamp = await data.GetLastTimeStamp(cancel);

        // A marker that says nothing is not one to compare against: the probe is skipped this
        // once rather than read as the database having moved.
        if (timeStamp.Length == 0)
        {
            return null;
        }

        return timeStamp;
    };

    return options;
}
```
<sup><a href='/src/Scry.Server.Delta/ScryDeltaExtensions.cs#L56-L102' title='Snippet source file'>snippet source</a> | <a href='#snippet-useDeltaChanges' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Docs: [Caching and 304 Not Modified](https://github.com/Papyrine/Scry/blob/main/docs/caching.md), [Live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md)
