namespace Scry;

/// <summary>
/// The answers to a <see cref="QueryBatchRequest"/>, one per entry and in the order they were sent.
/// The envelope succeeds whenever the batch itself was well-formed; a per-entry failure is reported
/// in its own <see cref="QueryBatchResult"/> rather than failing the request.
/// </summary>
// begin-snippet: wireBatchResponse
public sealed record QueryBatchResponse(int Version, IReadOnlyList<QueryBatchResult> Results)
{
    /// <summary>Creates a batch response stamped with the current <see cref="WireFormat.Version"/>.</summary>
    public static QueryBatchResponse Create(IReadOnlyList<QueryBatchResult> results) =>
        new(WireFormat.Version, results);

    /// <summary>
    /// The server's schema stamp, carried once for the whole batch — every entry was answered by the
    /// same model. Serves the same drift detection <see cref="QueryResponse.Stamp"/> does.
    /// </summary>
    public string? Stamp { get; init; }

    /// <summary>
    /// The raw binary parts a <see cref="ScryBinary.ContentType"/> response arrived with. A batch
    /// numbers its parts globally across entries, so the one list serves every result. Never
    /// serialized — the parts travel beside the JSON, not inside it.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<byte[]>? BinaryParts { get; init; }
}
// end-snippet
