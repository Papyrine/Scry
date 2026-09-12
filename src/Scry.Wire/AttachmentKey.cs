namespace Scry;

/// <summary>
/// One value of the row's primary key. Mirrors <c>ConstNode</c>: the invariant-culture string form
/// plus the shape the client had, which the server treats as a hint and never as an instruction — the
/// value is parsed into the key member's own CLR type.
/// </summary>
/// <remarks>
/// Keys are positional, ordered by member name ordinal — the order the generator and the server both
/// derive independently, since a composite key's declared order is not visible to the metadata reader.
/// </remarks>
// begin-snippet: wireAttachmentRequest
public sealed record AttachmentKey(string? Value, ClrTypeTag Tag)
{
    // The wire's constructor: only the members a request has to carry. The value may be absent, and
    // reaches the reader through its init accessor instead, since an optional parameter would have to
    // trail and the declared order is the one callers write.
    [JsonConstructor]
    public AttachmentKey(ClrTypeTag tag) :
        this(null, tag)
    {
    }
}
// end-snippet