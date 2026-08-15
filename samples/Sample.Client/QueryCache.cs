using System.Collections.Concurrent;

namespace Sample.Client;

/// <summary>
/// What the client remembers of the responses it has already been given, so a <c>304</c> has something
/// to stand for. Held apart from <see cref="QueryCacheHandler"/> because the factory rotates handlers
/// and a cache that rotated with them would forget everything every couple of minutes.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the fingerprint <c>ScryClient</c> already attaches to every request it sends, paired with
/// the endpoint it was sent to — the same body means something different at <c>…/batch</c> than it does
/// at the query endpoint. The fingerprint is of the request bytes, so two spellings of one query miss
/// rather than collide: a miss costs a round trip, a collision would cost correctness.
/// </para>
/// <para>
/// Unbounded, which suits a sample whose pages ask the same handful of queries. A real one wants a
/// bound — an LRU, or a cap on the bytes held — and has to be <see cref="Clear"/>ed whenever the user's
/// identity changes, since a response is only ever cacheable against the identity that fetched it.
/// </para>
/// </remarks>
public sealed class QueryCache
{
    ConcurrentDictionary<string, CachedResponse> entries = new();

    /// <summary>Responses served from this cache, i.e. requests the server answered with a 304.</summary>
    public int Hits => hits;

    int hits;

    /// <summary>Requests that reached the server and came back with a body.</summary>
    public int Misses => misses;

    int misses;

    public CachedResponse? Get(string key) =>
        entries.GetValueOrDefault(key);

    public void Store(string key, CachedResponse response) =>
        entries[key] = response;

    public void RecordHit() =>
        Interlocked.Increment(ref hits);

    public void RecordMiss() =>
        Interlocked.Increment(ref misses);

    /// <summary>Drops everything held. Call on sign-in, sign-out, or any other change of identity.</summary>
    public void Clear() =>
        entries.Clear();
}

/// <summary>One response kept whole, alongside the ETag that says whether it is still current.</summary>
public sealed record CachedResponse(string ETag, byte[] Body, string ContentType, string? Stamp);
