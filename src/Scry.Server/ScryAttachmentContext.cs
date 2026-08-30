namespace Scry;

/// <summary>
/// Context handed to an <see cref="IAttachmentPolicy{T}"/>: which member is being fetched, off which
/// row, and the request services and headers the decision may read.
/// </summary>
// begin-snippet: attachmentContext
public sealed class ScryAttachmentContext(
    IServiceProvider services,
    DbContext db,
    string member,
    IReadOnlyList<object> keyValues,
    IHeaderDictionary requestHeaders,
    IHeaderDictionary responseHeaders)
{
    /// <summary>Context for a processor hosted outside the HTTP endpoint, which has no headers.</summary>
    public ScryAttachmentContext(IServiceProvider services, DbContext db, string member, IReadOnlyList<object> keyValues) :
        this(services, db, member, keyValues, new HeaderDictionary(), new HeaderDictionary())
    {
    }

    /// <summary>The request-scoped service provider (e.g. for the current user/tenant).</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The active <see cref="DbContext"/>.</summary>
    public DbContext Db { get; } = db;

    /// <summary>The attachment member being fetched, as the schema names it.</summary>
    public string Member { get; } = member;

    /// <summary>
    /// The primary key of the row the value hangs off, parsed into the key members' own CLR types and
    /// ordered by member name — the order the schema derives, not the order EF declares.
    /// </summary>
    /// <remarks>
    /// The row has not been read at this point, and may not exist: these are the key a caller asked
    /// for, not one taken from a row. Authorizing on them alone is the fast path; a decision needing
    /// the row itself can read it through <see cref="Db"/>.
    /// </remarks>
    public IReadOnlyList<object> KeyValues { get; } = keyValues;

    /// <summary>
    /// The headers the caller sent. Client-supplied and therefore untrusted — hint data, never an
    /// authorization input.
    /// </summary>
    public IHeaderDictionary RequestHeaders { get; } = requestHeaders;

    /// <summary>The headers of the response being built. Writes here reach the client.</summary>
    public IHeaderDictionary ResponseHeaders { get; } = responseHeaders;

    /// <summary>
    /// What this row's bytes will be served as. Starts as what <c>[Attachment(ContentType = ...)]</c>
    /// declared — null where it declared nothing, which is served as
    /// <see cref="AttachmentMedia.Default"/> — and assigning here overrides it for this fetch alone.
    /// </summary>
    /// <remarks>
    /// The hook for a column holding more than one kind of thing, where the type belongs to the row
    /// rather than to the member: read it off a sibling column through <see cref="Db"/>, keyed by
    /// <see cref="KeyValues"/>. Ignored when the fetch does not reach a 200 — a refused, missing, or
    /// null value carries no body to label.
    /// </remarks>
    public string? ContentType { get; set; }
}
// end-snippet
