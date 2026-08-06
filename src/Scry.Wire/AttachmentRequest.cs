namespace Scry;

/// <summary>
/// A request for one <c>[Attachment]</c> member's value: the source it hangs off, the member's name,
/// and the primary key of the row holding it. Sent to the attachment endpoint, which answers with the
/// raw bytes rather than a query response — an attachment is fetched, never queried.
/// </summary>
// begin-snippet: wireAttachmentRequest
public sealed record AttachmentRequest(int Version, string Root, string Member, IReadOnlyList<AttachmentKey> Keys)
{
    /// <summary>The current attachment request version. Versioned apart from the query wire, which this does not touch.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Creates a request stamped with <see cref="CurrentVersion"/>.</summary>
    public static AttachmentRequest Create(string root, string member, IReadOnlyList<AttachmentKey> keys, string? stamp = null) =>
        new(CurrentVersion, root, member, keys)
        {
            Stamp = stamp
        };

    /// <summary>
    /// The schema stamp of the generated client model the handle came from, when known. Read for the
    /// same reason <see cref="QueryRequest.Stamp"/> is — to attribute a rejection to a stale client —
    /// and never as an authorization input.
    /// </summary>
    public string? Stamp { get; init; }
}

/// <summary>
/// One value of the row's primary key. Mirrors <c>ConstNode</c>: the invariant-culture string form
/// plus the shape the client had, which the server treats as a hint and never as an instruction — the
/// value is parsed into the key member's own CLR type.
/// </summary>
/// <remarks>
/// Keys are positional, ordered by member name ordinal — the order the generator and the server both
/// derive independently, since a composite key's declared order is not visible to the metadata reader.
/// </remarks>
public sealed record AttachmentKey(string? Value, ClrTypeTag Tag);
// end-snippet
