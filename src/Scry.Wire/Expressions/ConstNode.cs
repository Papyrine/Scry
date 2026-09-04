namespace Scry;

/// <summary>
/// A literal constant. <see cref="Value"/> is the invariant-culture string form (null for a null
/// constant); the server reconciles it with the member type at the comparison site.
/// </summary>
public sealed record ConstNode(string? Value, ClrTypeTag Tag) :
    Node
{
    // The wire's constructor: only the members a request has to carry. The value may be absent, and
    // reaches the reader through its init accessor instead, since an optional parameter would have to
    // trail and the declared order is the one callers write.
    [JsonConstructor]
    public ConstNode(ClrTypeTag tag) :
        this(null, tag)
    {
    }
}