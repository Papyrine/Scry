namespace Scry;

/// <summary>The opening or closing line of a streamed result.</summary>
public sealed record ScryStreamMarker
{
    [JsonPropertyName(ScryStream.MarkerProperty)]
    public required string Kind { get; init; }

    /// <summary>Wire version, on the opening marker only.</summary>
    public int? Version { get; init; }

    /// <summary>The server's schema stamp, on the opening marker only.</summary>
    public string? Stamp { get; init; }

    /// <summary>Why the stream ended early, on a <see cref="ScryStream.Error"/> marker only.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Renamed enum values, on the opening marker only and only when the request's stamp differs from
    /// the server's — the same rule a single response follows, so rows read identically either way.
    /// </summary>
    public IReadOnlyList<EnumAlias>? EnumAliases { get; init; }
}