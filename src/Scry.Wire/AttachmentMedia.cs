namespace Scry;

/// <summary>
/// What an attachment's bytes are served as, and the file name extension that follows from it.
/// </summary>
/// <remarks>
/// Shared because two clients name the same download: the debug sidecar, which reads the media type
/// off the response it just received, and the explorer, which reads it out of introspection before
/// the fetch is made. Both fall back to <c>.bin</c>, which is the honest name for bytes whose type
/// the model never stated.
/// </remarks>
public static class AttachmentMedia
{
    /// <summary>
    /// What an attachment with no declared content type is served as: bytes, and nothing said about
    /// them.
    /// </summary>
    public const string Default = "application/octet-stream";

    static readonly Dictionary<string, string> extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        {"image/png", ".png"},
        {"image/jpeg", ".jpg"},
        {"image/gif", ".gif"},
        {"image/webp", ".webp"},
        {"image/avif", ".avif"},
        {"image/svg+xml", ".svg"},
        {"image/bmp", ".bmp"},
        {"image/tiff", ".tiff"},
        {"application/pdf", ".pdf"},
        {"application/zip", ".zip"},
        {"application/json", ".json"},
        {"application/xml", ".xml"},
        {"text/plain", ".txt"},
        {"text/csv", ".csv"},
        {"text/html", ".html"},
        {"audio/mpeg", ".mp3"},
        {"video/mp4", ".mp4"}
    };

    /// <summary>
    /// The extension to save <paramref name="contentType"/> under, leading dot included. Anything
    /// unrecognized — including null, and a type the map does not carry — is <c>.bin</c>: a wrong
    /// extension is worse than a generic one, since it is the operating system's cue for what to open
    /// the file with.
    /// </summary>
    public static string Extension(string? contentType)
    {
        if (contentType is null)
        {
            return ".bin";
        }

        // Parameters are part of the header, not of the type: "text/plain; charset=utf-8" names the
        // same thing "text/plain" does.
        var media = contentType.Split(';')[0].Trim();
        return extensions.GetValueOrDefault(media, ".bin");
    }
}
