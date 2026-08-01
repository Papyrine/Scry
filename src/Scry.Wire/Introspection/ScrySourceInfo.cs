namespace Scry;

/// <summary>A queryable source (the root of a query): its name, kind, and the model type it yields.</summary>
public sealed record ScrySourceInfo(string Name, string Kind, string Model)
{
    /// <summary>
    /// The deprecation the model declares on the source's CLR type with <c>[Obsolete]</c>, in the same
    /// form as <see cref="ScryMemberInfo.Obsolete"/>. Carried on the source as well as the type so the
    /// entry point a query starts from warns without traversing a member.
    /// </summary>
    public string? Obsolete { get; init; }
}