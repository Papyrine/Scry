namespace Scry;

/// <summary>
/// Attaches the server-side authorization check for a type's <see cref="AttachmentAttribute"/>
/// members. The named type must implement <c>IAttachmentPolicy&lt;T&gt;</c> and is consulted on every
/// fetch, before the row is read. Client-irrelevant.
/// </summary>
/// <remarks>
/// A type carrying an attachment must have one, either here or through
/// <c>ScryOptions.AddAttachmentPolicy</c>; a server whose model exposes an attachment with no check
/// refuses to start. The check is not a substitute for a row policy — both apply, and the fetch reads
/// its row through the policy-filtered source, so a row a query could not return is not one an
/// attachment can be pulled from. Like a row policy, it is inherited: a subclass cannot shed the one
/// its base carries.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class AttachmentWithAttribute(Type policy) :
    Attribute
{
    public Type Policy { get; } = policy;
}
