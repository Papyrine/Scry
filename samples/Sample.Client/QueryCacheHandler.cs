using System.Net;

namespace Sample.Client;

/// <summary>
/// The client half of the 304 exchange: re-asks with <c>If-None-Match</c>, and rebuilds the response a
/// <c>304</c> stands for out of <see cref="QueryCache"/>. Nothing above this handler —
/// <c>ScryClient</c>, the pages, the generated models — ever learns a round trip was saved.
/// </summary>
/// <remarks>
/// Only <c>application/json</c> responses are kept. A streamed result is meant to be read a row at a
/// time and a multipart one carries binary parts beside its envelope; both would have to be buffered
/// whole to be cached, so both pass through untouched.
/// </remarks>
// begin-snippet: clientCacheHandler
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
    // end-snippet

    /// <summary>
    /// Rebuilds the response the 304 stands for. The body is the cached one; the headers are this
    /// exchange's, so whatever the server said just now is what the client above sees — falling back to
    /// the cached schema stamp when the 304 carried none.
    /// </summary>
    static HttpResponseMessage Replay(HttpRequestMessage request, HttpResponseMessage response, CachedResponse cached)
    {
        var replayed = WithBody(request, response, cached.Body, cached.ContentType);
        if (cached.Stamp is { } stamp &&
            !replayed.Headers.Contains(WireFormat.SchemaStampHeader))
        {
            replayed.Headers.TryAddWithoutValidation(WireFormat.SchemaStampHeader, stamp);
        }

        return replayed;
    }

    static HttpResponseMessage WithBody(
        HttpRequestMessage request,
        HttpResponseMessage response,
        byte[] body,
        string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new(contentType);

        var rebuilt = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
            RequestMessage = request,
            Version = response.Version
        };

        foreach (var header in response.Headers)
        {
            rebuilt.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Dispose();
        return rebuilt;
    }

    static string? Stamp(HttpResponseMessage response) =>
        response.Headers.TryGetValues(WireFormat.SchemaStampHeader, out var values)
            ? values.FirstOrDefault()
            : null;

    /// <summary>
    /// The cache key: the endpoint, and the fingerprint the client attached to the body it is sending.
    /// Null for a request carrying no fingerprint, which is one this handler leaves alone.
    /// </summary>
    static string? Key(HttpRequestMessage request)
    {
        // A query asked as a URL is identified by that URL — which is the whole point of asking as one,
        // and is why the browser's own cache can answer a repeat without this handler existing. Kept
        // here anyway: a non-browser host has no such cache, and the sample runs in both.
        if (request.Method == HttpMethod.Get)
        {
            return request.RequestUri?.ToString();
        }

        if (request.Content is not { } content ||
            !content.Headers.TryGetValues(WireFormat.QueryHashHeader, out var values) ||
            values.FirstOrDefault() is not { } fingerprint)
        {
            return null;
        }

        return $"{request.RequestUri}|{fingerprint}";
    }
}
