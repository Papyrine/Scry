namespace Scry;

/// <summary>
/// Attaches a server-side row/instance policy to a queryable type. The named type must implement
/// <c>IReturnablePolicy&lt;T&gt;</c> and is resolved and applied by the server before any client
/// predicate, so client filters can only narrow the already-authorized set. Client-irrelevant.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ReturnableWithAttribute(Type policy) :
    Attribute
{
    public Type Policy { get; } = policy;

    /// <summary>What a row this policy denies produces where the query returns a single row.</summary>
    public DeniedRowMode RootSingle { get; set; }

    /// <summary>
    /// What a row this policy denies produces where the query returns rows — a list, a page, a stream,
    /// or a count or aggregate folded over them.
    /// </summary>
    public DeniedRowMode RootList { get; set; }

    /// <summary>What a row this policy denies produces where a navigation steps into it.</summary>
    public DeniedRowMode Navigation { get; set; }

    /// <summary>
    /// What a <c>[QueryableCollection]</c> of this policy's type does. <see cref="DeniedCollectionMode.Hide"/>
    /// by default: the collection is read through the policy, as a navigation into the type is.
    /// </summary>
    public DeniedCollectionMode CollectionNavigation { get; set; } = DeniedCollectionMode.Hide;
}
