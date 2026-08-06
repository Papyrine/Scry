namespace Scry;

/// <summary>
/// One query as <see cref="ScryProcessor"/> handled it, reported to every registered
/// <see cref="IScryAuditor"/>. <see cref="Error"/> carries the real failure message — including for
/// outcomes the HTTP endpoint reports to the client as a generic 500 — so the audit trail is where
/// execution failures are readable.
/// </summary>
/// <param name="Request">
/// The deserialized request, exactly as validated — the full query AST. Null for an attachment fetch,
/// which carries no query; <see cref="Attachment"/> describes that instead, and exactly one of the two
/// is ever set.
/// </param>
/// <param name="Outcome">How the query ended.</param>
/// <param name="Duration">
/// Validation through completion. For a stream this spans the whole read, not just query start-up.
/// </param>
// begin-snippet: auditEntry
public sealed record ScryAuditEntry(
    QueryRequest? Request,
    ScryQueryOutcome Outcome,
    TimeSpan Duration)
{
    /// <summary>
    /// The attachment fetched, when the entry describes one rather than a query: which member of which
    /// source, and the row key it was asked for. Null for a query.
    /// </summary>
    /// <remarks>
    /// Worth watching on its own. An attachment is reached by row key through an endpoint of its own,
    /// so a run of rejected or not-found fetches is what key-guessing looks like.
    /// </remarks>
    public AttachmentRequest? Attachment { get; init; }

    /// <summary>The result shape, when the query succeeded; null when it never produced one.</summary>
    public ResultKind? Kind { get; init; }

    /// <summary>Whether the rows were streamed rather than materialized into a response.</summary>
    public bool Streamed { get; init; }

    /// <summary>
    /// Rows delivered: a list or page's count, 0 or 1 for a single row, the rows read for a stream —
    /// including one that ended early. Null where rows are not the result (a scalar) or the query
    /// never ran.
    /// </summary>
    public int? Rows { get; init; }

    /// <summary>The rejection or failure message; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// True when a rejection was attributed to a stale client (a schema stamp differing from the
    /// server's) rather than an invalid query — the benign explanation. A rejection without it is
    /// the one worth watching.
    /// </summary>
    public bool StaleClient { get; init; }
}
// end-snippet
