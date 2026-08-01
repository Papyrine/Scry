namespace Scry;

/// <summary>
/// Several independent queries sent as one request. Each entry is an ordinary
/// <see cref="QueryRequest"/> — the batch adds no query vocabulary, only a way to carry more than one
/// — and the server validates, policy-filters, and executes every one of them separately.
/// </summary>
// begin-snippet: wireBatchRequest
public sealed record QueryBatchRequest(int Version, IReadOnlyList<QueryRequest> Queries)
{
    /// <summary>Creates a batch stamped with the current <see cref="WireFormat.Version"/>.</summary>
    public static QueryBatchRequest Create(IReadOnlyList<QueryRequest> queries) =>
        new(WireFormat.Version, queries);
}
// end-snippet
