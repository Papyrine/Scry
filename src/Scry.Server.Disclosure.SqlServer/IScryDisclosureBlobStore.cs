namespace Scry;

/// <summary>
/// Somewhere other than the database to keep disclosed content: object storage, a file share. The
/// SQL Server store hands it each piece of content by address, asks for it back when an event is
/// rebuilt, and tells it to forget one when a row is erased.
/// </summary>
/// <remarks>
/// Content is named by its address, which is a hash of it, so a put of something already held is a
/// put of the same bytes and may be a no-op. A put has to be durable before it returns: the record
/// that names the content is committed only after.
/// </remarks>
// begin-snippet: disclosureBlobStore
public interface IScryDisclosureBlobStore
{
    /// <summary>Keeps a piece of content under its address.</summary>
    ValueTask PutAsync(ScryDisclosureAddress address, ReadOnlyMemory<byte> bytes, Cancel cancel);

    /// <summary>The content kept under an address, or null where there is none.</summary>
    ValueTask<ReadOnlyMemory<byte>?> GetAsync(ScryDisclosureAddress address, Cancel cancel);

    /// <summary>Forgets the content kept under an address. Not an error where there is none.</summary>
    ValueTask DeleteAsync(ScryDisclosureAddress address, Cancel cancel);
}
// end-snippet
