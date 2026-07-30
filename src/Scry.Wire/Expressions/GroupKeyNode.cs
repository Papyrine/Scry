namespace Scry.Wire;

/// <summary>
/// The key a query grouped by, read inside the projection or group filter that follows. Only valid
/// there. <see cref="Index"/> selects the part of a composite key, and is zero for a single one.
/// </summary>
/// <remarks>
/// A key that is a plain member is named by its own <see cref="MemberNode"/> instead — the path is
/// what the server matches it back by, and saying it that way keeps an existing client's requests
/// unchanged. This node exists for the keys that have no path to name: a key computed from an
/// expression, where the only thing to say is which of the query's keys is meant.
/// </remarks>
public sealed record GroupKeyNode(int Index) :
    Node;
