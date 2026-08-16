/// <summary>
/// Answers a repeated query with <c>304 Not Modified</c> when neither the query nor the database has
/// changed since the client last asked. The database half of that question is answered by
/// <see href="https://github.com/SimonCropp/Delta">Delta</see>, whose <c>GetLastTimeStamp</c> reads the
/// server's own change marker; the query half is the query itself, which a URL carries.
/// </summary>
/// <remarks>
/// <para>
/// Only a query asked <see cref="QueryUrl">as a URL</see> is answered conditionally, which is not a
/// restriction so much as the shape of the problem: a URL identifies a response, and that is the whole
/// reason a cache — this middleware included — can key on one. A query too long for a URL travels as a
/// body and is answered exactly as it was before, since nothing between the client and here would have
/// cached that response anyway.
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
/// The fingerprint is computed here, from the encoded request in the URL, rather than taken from
/// anything the client asserts about its own query. It therefore identifies the query as well as the
/// query does, and a client cannot describe its request as something other than what it sent.
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
    // end-snippet

    /// <summary>
    /// Identifies the query a URL carries. The encoded request is the query verbatim, so hashing it is
    /// only about size: it runs to thousands of characters where an ETag wants a handful. Twelve bytes
    /// of SHA-256 is sixteen base64url characters, and for a cache one client keeps the odds of two of
    /// its own queries colliding are about n²/2^97.
    /// </summary>
    static string? Fingerprint(HttpRequest request)
    {
        if (request.Query[QueryUrl.Parameter].ToString() is not {Length: > 0} encoded)
        {
            return null;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(encoded), hash);
        return Base64Url.EncodeToString(hash[..12]);
    }

    static string Etag(string schemaStamp, string timeStamp, string fingerprint, string? suffix)
    {
        if (suffix is null)
        {
            return $"\"{schemaStamp}-{timeStamp}-{fingerprint}\"";
        }

        return $"\"{schemaStamp}-{timeStamp}-{fingerprint}-{suffix}\"";
    }
}
