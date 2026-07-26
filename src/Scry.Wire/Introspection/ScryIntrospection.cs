namespace Scry.Wire;

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

    /// <summary>The query endpoint the explorer POSTs translated requests to (set by the explorer host).</summary>
    public string QueryEndpoint { get; init; } = "/api/query";
}