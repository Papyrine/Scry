namespace Scry.Wire;

/// <summary>
/// A serialized query: the root source name plus an ordered pipeline of operators. This is the
/// payload sent from client to server.
/// </summary>
public sealed record QueryRequest(int Version, string Root, IReadOnlyList<QueryOp> Pipeline)
{
    /// <summary>Creates a request stamped with the current <see cref="WireFormat.Version"/>.</summary>
    public static QueryRequest Create(string root, IReadOnlyList<QueryOp> pipeline) =>
        new(WireFormat.Version, root, pipeline);
}
