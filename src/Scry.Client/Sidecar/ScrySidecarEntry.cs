namespace Scry;

/// <summary>One captured HTTP exchange, as recorded by <see cref="ScrySidecarHandler"/>.</summary>
/// <remarks>
/// Note what a logical query is not: one entry. The client retries a refused GET as a POST, so one
/// <c>ToListAsync()</c> can appear twice; a batch collapses several queries into one entry; a stream
/// is one entry producing many rows.
/// </remarks>
public sealed record ScrySidecarEntry
{
    public required int Id { get; init; }

    public required DateTimeOffset Started { get; init; }

    /// <summary>
    /// Time to the buffered body for queries and batches. Streams and attachments are never
    /// buffered, so theirs is time to response headers only.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    public required string Method { get; init; }

    /// <summary>The absolute request URL — what the attachment download action re-sends to.</summary>
    public required string Url { get; init; }

    public required ScrySidecarKind Kind { get; init; }

    /// <summary>
    /// The decoded wire request for a query entry — from the URL's <c>q</c> parameter on a GET,
    /// from the body on a POST. Drives the source-name column and the explorer link.
    /// </summary>
    public QueryRequest? Request { get; init; }

    /// <summary>The request pretty-printed as JSON, for display.</summary>
    public string? RequestJson { get; init; }

    public required IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; init; } = [];

    public int? Status { get; init; }

    public string? ReasonPhrase { get; init; }

    /// <summary>
    /// The response body pretty-printed, for buffered kinds. A multipart response shows its JSON
    /// envelope with the <c>$bin</c> references intact — the parts themselves are listed by size in
    /// <see cref="BinaryPartSizes"/> rather than inlined as base64.
    /// </summary>
    public string? ResponseJson { get; init; }

    /// <summary>Byte sizes of a multipart response's binary parts, in wire order.</summary>
    public IReadOnlyList<int>? BinaryPartSizes { get; init; }

    /// <summary>The transport exception's message, or the server's error for a non-success status.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The exact POST body of an attachment request, kept so the download action can re-send it.
    /// The response bytes are deliberately not kept — downloading always re-asks the server.
    /// </summary>
    public byte[]? AttachmentRequestBody { get; init; }
}
