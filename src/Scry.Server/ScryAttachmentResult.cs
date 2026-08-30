namespace Scry;

/// <summary>The outcome of fetching one attachment.</summary>
/// <remarks>
/// A refusal, a row that is not there, and a row a policy hides are deliberately the same answer.
/// Telling them apart would make the endpoint an oracle for which rows exist, which is exactly what a
/// caller holding a guessed key is asking.
/// </remarks>
public sealed record ScryAttachmentResult
{
    /// <summary>Whether the value was handed over. False covers denied, absent, and policy-filtered alike.</summary>
    public required bool Found { get; init; }

    /// <summary>
    /// The bytes, or null where the row was readable but the column holds nothing. Only meaningful
    /// when <see cref="Found"/>; a null here is a value that is absent, not a row that is.
    /// </summary>
    public byte[]? Value { get; init; }

    /// <summary>
    /// What the bytes are: what the member declared, or what the attachment policy overrode it with.
    /// Null where neither said, which the endpoint serves as <see cref="AttachmentMedia.Default"/>.
    /// </summary>
    /// <remarks>
    /// Carried on the result rather than left to the endpoint so a transport of its own gets the same
    /// answer the HTTP one does, without re-reading the schema to find it.
    /// </remarks>
    public string? ContentType { get; init; }

    /// <summary>The answer for everything a caller may not have: refused, missing, or hidden.</summary>
    public static ScryAttachmentResult NotFound { get; } = new()
    {
        Found = false
    };
}
