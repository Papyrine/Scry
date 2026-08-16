namespace Scry;

/// <summary>
/// Flattens a collection navigation into a sequence of its elements, which every later operator then
/// reads. <see cref="Path"/> names the collection — reference navigations may precede it, the
/// collection is always the last segment.
/// </summary>
/// <remarks>
/// Unlike a <see cref="SubqueryNode"/>, which folds a collection to a scalar, this replaces the row
/// being queried. The element type is allow-listed in its own right and, because a
/// <c>[QueryableCollection]</c> of a policied type is refused at startup, carries no row policy that
/// the flatten could bypass.
/// </remarks>
public sealed record SelectManyOp(
    [property: JsonConverter(typeof(PathConverter))] IReadOnlyList<string> Path) :
    QueryOp;
