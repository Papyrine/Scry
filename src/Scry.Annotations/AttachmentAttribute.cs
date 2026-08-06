namespace Scry;

// begin-snippet: attachment
/// <summary>
/// Marks a <c>byte[]</c> member as a claim check: the query never reads it, and the generated client
/// member becomes a handle that fetches the value on demand through the attachment endpoint. Unlike
/// <see cref="BinaryTransferAttribute"/>, which only changes how a value is encoded in the response,
/// this changes the client-visible shape — so it moves the schema stamp.
/// </summary>
/// <remarks>
/// Valid only on a <c>byte[]</c> member of a <c>[Queryable]</c> entity, whose primary key the fetch is
/// keyed by. A view or POCO source has no key to look a row up by, a member that is not a
/// <c>byte[]</c> has nothing to stream, and combining this with <see cref="BinaryTransferAttribute"/>
/// asks for a value to be both fetched and not fetched. Each fails the build, and again at server
/// startup. A source carrying one is unreadable until an <c>IAttachmentPolicy&lt;T&gt;</c> authorizes
/// it — see <see cref="AttachmentWithAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class AttachmentAttribute :
    Attribute;
// end-snippet
