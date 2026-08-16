namespace Scry;

/// <summary>
/// Attached by the generator to a query model, or to one of its members, that the server's model marks
/// <c>[Sensitive]</c>. It is what lets a client decide — before it sends anything — that a query has
/// to travel in a body rather than in a URL.
/// </summary>
/// <remarks>
/// <para>
/// A separate attribute from the model's own <c>[Sensitive]</c> because a client project never
/// references the annotations: it is pointed at the model DLL by path, and the generator reads that
/// metadata rather than linking against it. This is the same fact, re-declared where the client can
/// see it.
/// </para>
/// <para>
/// On a class it means every member read off that class, which is how a <c>[QueryableComplex]</c> type
/// is covered — one carries no <c>[ScryModel]</c>, so a list of names would have nowhere to live.
/// Nothing trusts this: the server re-derives sensitivity from its own model on every request.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class ScrySensitiveAttribute :
    Attribute;
