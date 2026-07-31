namespace Scry;

/// <summary>
/// Context handed to an <see cref="IReturnablePolicy{T}"/>, exposing the request services and the
/// call's HTTP headers.
/// </summary>
// begin-snippet: policyContext
public sealed class ScryPolicyContext(
    IServiceProvider services,
    DbContext db,
    IHeaderDictionary requestHeaders,
    IHeaderDictionary responseHeaders)
{
    /// <summary>Context for a processor hosted outside the HTTP endpoint, which has no headers.</summary>
    public ScryPolicyContext(IServiceProvider services, DbContext db) :
        this(services, db, new HeaderDictionary(), new HeaderDictionary())
    {
    }

    /// <summary>The request-scoped service provider (e.g. for the current user/tenant).</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The active <see cref="DbContext"/>.</summary>
    public DbContext Db { get; } = db;

    /// <summary>
    /// The headers the caller sent. Client-supplied and therefore untrusted — hint data, never an
    /// authorization input.
    /// </summary>
    public IHeaderDictionary RequestHeaders { get; } = requestHeaders;

    /// <summary>The headers of the response being built. Writes here reach the client.</summary>
    public IHeaderDictionary ResponseHeaders { get; } = responseHeaders;
}
// end-snippet
