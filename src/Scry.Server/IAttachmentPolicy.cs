namespace Scry;

/// <summary>
/// The authorization check for a type's <c>[Attachment]</c> members, consulted on every fetch before
/// the row is read. Register via <see cref="ScryOptions.AddAttachmentPolicy{TEntity,TPolicy}"/> or the
/// <c>[AttachmentWith]</c> attribute; a source exposing an attachment with neither refuses to start.
/// </summary>
/// <remarks>
/// This is a yes/no decision about one member of one row, which is why it is not an
/// <see cref="IReturnablePolicy{T}"/>: there is no set to narrow, only a value to hand over or not.
/// The two compose rather than substitute — the fetch reads its row through the policy-filtered
/// source, so a row policy still hides what it hides, and this decides the rest.
/// </remarks>
// begin-snippet: attachmentPolicyInterface
public interface IAttachmentPolicy<T>
{
    bool Authorize(ScryAttachmentContext context);
}
// end-snippet
