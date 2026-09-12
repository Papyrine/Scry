namespace Scry;

/// <summary>
/// One projected member of a join, naming the side it reads from. <see cref="Path"/> is the member
/// read off that side; for a <see cref="JoinKind.Group"/> join the inner side is a group rather than
/// a row, so its members carry an <see cref="Aggregate"/> and an empty path instead.
/// </summary>
public sealed record JoinMember(
    string Name,
    JoinSide Side,
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path)
{
    /// <summary>Folds the inner group to a scalar. Only valid on the inner side of a group join.</summary>
    public AggregateNode? Aggregate { get; init; }
}
