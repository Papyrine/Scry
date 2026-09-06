using Microsoft.Net.Http.Headers;

/// <summary>
/// Answers a repeated query with <c>304 Not Modified</c> while neither the query, the model, nor the
/// data behind it has changed — for a query asked as a URL, which is the only kind a cache can
/// identify.
/// </summary>
/// <remarks>
/// <para>
/// Dormant unless <see cref="ScryOptions.QueryFreshness"/> is configured. Without it a response
/// carries no <c>ETag</c> and nothing is ever answered conditionally, which is exactly what this
/// server did before any of it existed.
/// </para>
/// <para>
/// The ETag is <c>"{schemaStamp}-{freshness}-{query}-{scope}"</c> and each part earns its place: the
/// schema stamp so a deployment that changed the queryable surface is never answered from a cache the
/// previous one filled, the freshness token so a write invalidates every entry, the query so one
/// query's ETag is never accepted for another, and the scope so a response shaped for one caller is
/// never handed to the next.
/// </para>
/// </remarks>
static class QueryEtag
{
    /// <summary>
    /// Writes the <c>ETag</c> for a URL-borne query and reports whether the request was answered
    /// <c>304</c> — in which case the caller is done and must write nothing more.
    /// </summary>
    public static async ValueTask<bool> NotModified(HttpContext context, ScryProcessor processor, ScryOptions options)
    {
        if (options.QueryFreshness is not { } freshness ||
            Query(context.Request) is not { } query)
        {
            return false;
        }

        // A client that has just written asks not to be told its own stale answer, and says so the
        // standard way. Honoured because the alternative is worse than a slow query: the marker a
        // freshness source reads trails a commit, so inside that window a writer would be handed the
        // rows it just replaced.
        if (RequestHeaders(context).CacheControl is {NoCache: true})
        {
            return false;
        }

        if (await freshness(context, context.RequestAborted) is not { Length: > 0 } token)
        {
            return false;
        }

        var etag = Etag(processor.SchemaStamp, token, query, options.CacheScope?.Invoke(context));
        if (!Matches(context, etag))
        {
            // Only on a response that is about to carry rows. An ETag written here would otherwise end
            // up on a 400 as well, and a client that cached the rejection could later be told its copy
            // of it is still current.
            context.Response.OnStarting(
                state =>
                {
                    var response = (HttpResponse) state;
                    if (response.StatusCode == StatusCodes.Status200OK &&
                        !response.Headers.CacheControl.Any(_ => _ is not null && _.Contains("no-store", StringComparison.Ordinal)))
                    {
                        response.Headers.ETag = etag;
                    }

                    return Task.CompletedTask;
                },
                context.Response);
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status304NotModified;
        context.Response.Headers.ETag = etag;
        return true;
    }

    /// <summary>
    /// Whether the client's <c>If-None-Match</c> stands for the response about to be sent.
    /// </summary>
    /// <remarks>
    /// Parsed rather than string-compared, because all three of the shapes a naive comparison misses
    /// are ones a real deployment produces: a list, a <c>*</c>, and a tag some proxy weakened on the
    /// way through. RFC 9110 asks for the weak comparison here, and using it means a weakened tag
    /// still matches instead of turning every hit into a permanent miss.
    /// </remarks>
    static bool Matches(HttpContext context, string etag)
    {
        var conditions = RequestHeaders(context).IfNoneMatch;
        if (conditions.Count == 0)
        {
            return false;
        }

        // A bare "*" is deliberately not a match. The RFC reads it as "any current representation",
        // which for a query would answer 304 to a request whose query was never decoded — including
        // one the validator would have refused. No client sends it on a GET; a cache revalidates
        // with the tag it holds.
        var current = EntityTagHeaderValue.Parse(etag);
        return conditions.Any(_ => !_.Equals(EntityTagHeaderValue.Any) && _.Compare(current, useStrongComparison: false));
    }

    static Microsoft.AspNetCore.Http.Headers.RequestHeaders RequestHeaders(HttpContext context) =>
        context.Request.GetTypedHeaders();

    /// <summary>
    /// Identifies the query a URL carries. The encoded request is the query verbatim, so hashing it is
    /// only about size: it runs to thousands of characters where an ETag wants a handful. Twelve bytes
    /// of SHA-256 is sixteen base64url characters, and for a cache one client keeps the odds of two of
    /// its own queries colliding are about n²/2^97.
    /// </summary>
    internal static string? Query(HttpRequest request)
    {
        if (request.Query[QueryUrl.Parameter].ToString() is not {Length: > 0} encoded)
        {
            return null;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(encoded), hash);
        return Base64Url.EncodeToString(hash[..12]);
    }

    // The freshness token and the scope are hashed like the query is: a tag is stored by the
    // caller for as long as it caches, and the two are the database's write position and a tenant
    // or principal — neither is the caller's to read, and both only have to be stable and distinct.
    internal static string Etag(string schemaStamp, string freshness, string query, string? scope)
    {
        if (scope is null)
        {
            return $"\"{schemaStamp}-{Fingerprint(freshness)}-{query}\"";
        }

        return $"\"{schemaStamp}-{Fingerprint(freshness)}-{query}-{Fingerprint(scope)}\"";
    }

    static string Fingerprint(string value)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Base64Url.EncodeToString(hash[..12]);
    }
}
