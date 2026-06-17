namespace Scry;

/// <summary>
/// Attaches a server-side row/instance policy to a queryable type. The named type must implement
/// <c>IReturnablePolicy&lt;T&gt;</c> and is resolved and applied by the server before any client
/// predicate, so client filters can only narrow the already-authorized set. Client-irrelevant.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ReturnableWithAttribute(Type policyType) :
    Attribute
{
    public Type PolicyType { get; } = policyType;
}
