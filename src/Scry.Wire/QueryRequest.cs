namespace Scry;

/// <summary>
/// A serialized query: the root source name plus an ordered pipeline of operators. This is the
/// payload sent from client to server.
/// </summary>
// begin-snippet: wireRequest
public sealed record QueryRequest(int Version, string Root, IReadOnlyList<QueryOp> Pipeline)
{
    /// <summary>
    /// Creates a request stamped with the lowest <see cref="WireFormat"/> version that can carry its
    /// pipeline whole — see <see cref="WireFormat.RequiredVersion"/>.
    /// </summary>
    public static QueryRequest Create(string root, IReadOnlyList<QueryOp> pipeline, string? stamp = null) =>
        new(WireFormat.RequiredVersion(pipeline), root, pipeline)
        {
            Stamp = stamp
        };

    /// <summary>
    /// The schema stamp of the generated client model the query was written against, when known.
    /// Lets the server distinguish a stale client (generated against a different model) from an
    /// invalid query. Omitted on the wire when null. A request carries only what the vocabulary
    /// names — a member the server does not read is refused, not skipped — so a server that predates
    /// this member refuses a request carrying it, which is the fail-closed direction.
    /// </summary>
    public string? Stamp { get; init; }
}
// end-snippet
