namespace Scry;

// begin-snippet: wireSetKinds
/// <summary>How two sequences are combined.</summary>
public enum SetKind
{
    /// <summary>Rows of either side, deduplicated.</summary>
    Union,

    /// <summary>Rows of either side, keeping duplicates.</summary>
    Concat,

    /// <summary>Rows on both sides.</summary>
    Intersect,

    /// <summary>Rows on the first side that are not on the second.</summary>
    Except
}
// end-snippet
