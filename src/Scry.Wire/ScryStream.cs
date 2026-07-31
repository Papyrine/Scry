namespace Scry;

/// <summary>
/// The newline-delimited JSON format a streamed result travels in. One JSON value per line: a
/// <see cref="ScryStreamMarker"/> opening the stream, then one projected row per line, then a marker
/// closing it.
/// </summary>
/// <remarks>
/// The closing marker is what makes the format safe. A stream commits to a success status before its
/// first row is written, so a failure after that point cannot become a 400 or a 500 — and a truncated
/// response (a dropped connection, a faulting provider, a killed server) is otherwise
/// indistinguishable from a complete one. A reader that requires the closing marker sees the
/// difference, so a partial result is an error rather than a quietly short answer.
/// </remarks>
public static class ScryStream
{
    /// <summary>The media type a streamed response is served as.</summary>
    public const string ContentType = "application/x-ndjson";

    /// <summary>
    /// The property that tells a marker line from a row. Projected member names come from the client's
    /// own C# identifiers, and <c>$</c> cannot start one, so no row can collide with it.
    /// </summary>
    public const string MarkerProperty = "$scry";

    /// <summary>Opens the stream. Carries the version and schema stamp a single response carries.</summary>
    public const string Begin = "begin";

    /// <summary>Closes the stream. Its absence means the rows that arrived are not the whole result.</summary>
    public const string End = "end";

    /// <summary>Closes the stream after a failure part-way through, once a status is no longer changeable.</summary>
    public const string Error = "error";
}

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
