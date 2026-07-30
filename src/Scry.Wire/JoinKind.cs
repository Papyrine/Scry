namespace Scry.Wire;

// begin-snippet: wireJoinKinds
/// <summary>Which rows a join keeps.</summary>
public enum JoinKind
{
    /// <summary>Only rows with a match on both sides.</summary>
    Inner,

    /// <summary>Every outer row, with nulls where the inner side has no match.</summary>
    Left,

    /// <summary>Every inner row, with nulls where the outer side has no match.</summary>
    Right,

    /// <summary>
    /// Every outer row, paired with the matching inner rows as a group. The group is only ever
    /// aggregated — a projected group would make the response nested.
    /// </summary>
    Group
}

/// <summary>Which side of a join a projected member reads from.</summary>
public enum JoinSide
{
    Outer,
    Inner
}
// end-snippet
