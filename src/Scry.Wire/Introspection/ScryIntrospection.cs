namespace Scry;

/// <summary>
/// A read-only description of a Scry server's allow-listed query surface, served to tooling (the
/// query explorer) so it can reconstruct the generated client query models and drive IntelliSense.
/// Carries no security-sensitive detail (no policies, resolvers, or CLR internals).
/// </summary>
public sealed record ScryIntrospection(
    int Version,
    int MaxPageSize,
    IReadOnlyList<ScrySourceInfo> Sources,
    IReadOnlyList<ScryTypeInfo> Types,
    IReadOnlyList<ScryEnumInfo> Enums)
{
    /// <summary>Current introspection contract version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The query endpoint the explorer sends translated requests to (set by the explorer host).</summary>
    public string QueryEndpoint { get; init; } = "/api/query";

    /// <summary>
    /// The longest encoded query this server wants asked as a URL, or zero where it maps no GET route.
    /// </summary>
    /// <remarks>
    /// The same number <see cref="WireFormat.UrlLimitHeader"/> carries, published here because the
    /// explorer needs it before it has sent anything — and because the explorer is built and embedded
    /// when Scry is, so it can carry no per-deployment value of its own.
    /// </remarks>
    public int QueryUrlLimit { get; init; } = QueryUrl.MaxLength;

    /// <summary>
    /// Whether this host will show the SQL a request would run (set by the explorer host, from a guard
    /// of its own). False here — a processor describing itself makes no such offer; only the explorer
    /// endpoint does, and only where its own Development-only guard allows.
    /// </summary>
    public bool SqlPreview { get; init; }

    /// <summary>
    /// A hash of the queryable surface. Equals the generated client's <c>ScryQuery.SchemaStamp</c>
    /// exactly when client and server were built from the same model surface.
    /// </summary>
    public string SchemaStamp { get; init; } = "";
}