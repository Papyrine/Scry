/// <summary>
/// Answers a repeated query with <c>304 Not Modified</c> when neither the query nor the database has
/// changed since the client last asked. The database half of that question is answered by
/// <see href="https://github.com/SimonCropp/Delta">Delta</see>, whose <c>GetLastTimeStamp</c> reads the
/// server's own change marker; the query half is the fingerprint the client already sends.
/// </summary>
/// <remarks>
/// <para>
/// Delta's own <c>UseDelta</c> handles GET requests, where the URL identifies the response. Scry posts
/// its query as a body, so what identifies a response here is the request bytes rather than the path —
/// hence a middleware of its own, shaped like Delta's and doing the same thing to a different key.
/// </para>
/// <para>
/// The ETag is <c>"{schemaStamp}-{timeStamp}-{fingerprint}"</c>, and each part has to be there: the
/// schema stamp so a deployment that changed the queryable surface is never answered from a cache the
/// previous one filled, the timestamp so a write invalidates every entry, and the fingerprint so one
/// query's ETag is never accepted for another. Delta spells its first part as the entry assembly's last
/// write time; the schema stamp is the narrower version of the same idea — narrower because a
/// redeployed binary that left the surface alone keeps its caches. Where a response shape could change
/// without the surface changing, pass a build id as <paramref name="suffix"/>.
/// </para>
/// <para>
/// The fingerprint arrives from the client and is never trusted as more than a cache key — see
/// <see cref="QueryFingerprint"/>. A client that sends a wrong one can only be told that its own cached
/// response is still current, which is a lie it told itself: it can neither read another client's
/// response nor widen what this one is allowed to see.
/// </para>
/// <para>
/// Anything a response varies by that is <b>not</b> the query bytes belongs in
/// <paramref name="suffix"/> — the tenant an <see cref="IReturnablePolicy{T}"/> scopes rows to, the
/// principal an <c>[Attachment]</c> check answers for. Without it, a client whose identity changes
/// mid-session can be handed a 304 for rows the new identity was never shown.
/// </para>
/// </remarks>
public static class QueryEtagExtensions
{
    /// <summary>
    /// Adds conditional-request handling for requests under <paramref name="path"/>, which is the
    /// pattern <c>MapScry</c> was given: the query endpoint and the stream, batch, and attachment
    /// endpoints below it.
    /// </summary>
    // begin-snippet: queryEtagMiddleware
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
    // end-snippet

    static string Etag(string schemaStamp, string timeStamp, string fingerprint, string? suffix)
    {
        if (suffix is null)
        {
            return $"\"{schemaStamp}-{timeStamp}-{fingerprint}\"";
        }

        return $"\"{schemaStamp}-{timeStamp}-{fingerprint}-{suffix}\"";
    }
}
