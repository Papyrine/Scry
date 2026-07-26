namespace Scry.Wire;

/// <summary>Groups the sequence by one or more keys. A following <see cref="SelectOp"/> may use
/// aggregates and the group key.</summary>
public sealed record GroupByOp(IReadOnlyList<Node> Keys) :
    QueryOp;