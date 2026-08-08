namespace Scry;

// begin-snippet: scryAttachment
/// <summary>
/// A handle to one <c>[Attachment]</c> member's bytes: the claim rather than the value. Materialized
/// onto every row a query returns, carrying the row's key, and exchanged for the bytes only when
/// <see cref="OpenAsync"/> is called — a second request the server authorizes on its own.
/// </summary>
public sealed class ScryAttachment
{
    readonly ScryClient client;
    readonly string root;
    readonly string member;
    readonly IReadOnlyList<AttachmentKey> keys;

    internal ScryAttachment(ScryClient client, string root, string member, IReadOnlyList<AttachmentKey> keys)
    {
        this.client = client;
        this.root = root;
        this.member = member;
        this.keys = keys;
    }

    /// <summary>
    /// Fetches the bytes, or null when the stored value is null. The caller owns the returned stream
    /// and must dispose it. A row the server will not hand over — one the check denied, one a row
    /// policy hides, or one no longer there — is a <see cref="ScryRequestException"/> with status 404;
    /// the three are deliberately indistinguishable, so an unauthorized caller learns nothing about
    /// which rows exist.
    /// </summary>
    public Task<Stream?> OpenAsync(Cancel cancel = default) =>
        client.OpenAttachmentAsync(
            AttachmentRequest.Create(root, member, keys, client.SchemaStamp),
            cancel);
    // end-snippet

    /// <summary>The source the row was read from. Exposed for diagnostics; the server re-resolves it.</summary>
    public string Source => root;

    /// <summary>The member this handle stands for.</summary>
    public string Member => member;
}
