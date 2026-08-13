namespace Scry;

/// <summary>
/// One entry's outcome in a batch: either its <see cref="Response"/> or its <see cref="Error"/>,
/// never both. Entries are independent, so a rejected one leaves the rest of the batch answered.
/// </summary>
/// <remarks>
/// The member order is pinned for the reason <see cref="QueryBatchResponse"/>'s is: the batch endpoint
/// writes an entry itself, byte for byte against what serializing this produces.
/// </remarks>
// begin-snippet: wireBatchResult
public sealed record QueryBatchResult
{
    /// <summary>The result, when this entry succeeded. Null when it did not.</summary>
    [JsonPropertyOrder(0)]
    public QueryResponse? Response { get; init; }

    /// <summary>
    /// Why this entry was rejected or failed; null when it succeeded. Carries the specific message for
    /// a validation failure and the same fixed text a 500 would for anything else, so a batch leaks no
    /// more than the single-query endpoint does.
    /// </summary>
    [JsonPropertyOrder(1)]
    public string? Error { get; init; }

    /// <summary>
    /// The status this entry would have returned had it been sent on its own — 400 for a rejection,
    /// 500 for an execution failure. Entries are returned inside a successful envelope and so have no status of
    /// their own to inherit; carrying it here is what lets a client raise the same exception it would
    /// have for an unbatched query. 0 when the entry succeeded.
    /// </summary>
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Status { get; init; }

    /// <summary>
    /// True when this entry's rejection is attributed to a schema stamp differing from the server's.
    /// The typed client turns it into <see cref="ScryStaleClientException"/>, as it does for a
    /// single query.
    /// </summary>
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StaleClient { get; init; }
}
// end-snippet
