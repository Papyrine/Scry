/// <summary>How a member of a queryable type may be used.</summary>
enum MemberKind
{
    /// <summary>A scalar/enum/string value — usable in predicates, ordering and projection leaves.</summary>
    Scalar,

    /// <summary>A reference navigation to another queryable type — traversable in a member path.</summary>
    Navigation
}
