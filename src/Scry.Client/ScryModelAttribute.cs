namespace Scry.Client;

/// <summary>
/// Attached by the generator to each query model, naming the wire source the model stands for and
/// listing its scalar members. Lets a query that changes which row it reads — <c>OfType</c> narrowing
/// to a derived type, <c>SelectMany</c> flattening a collection — name the new source and project the
/// right members, neither of which the entry point it started from could supply.
/// </summary>
/// <remarks>
/// A hand-built source carries no attribute; such a query falls back to the source it was opened with
/// and, failing that, to the server's own default projection. Nothing trusts this: the name is
/// re-resolved against the server's allow-list on every request.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ScryModelAttribute(string source, params string[] members) :
    Attribute
{
    /// <summary>The wire name of the source this model stands for.</summary>
    public string Source { get; } = source;

    /// <summary>The model's scalar members, inherited ones included, in generated order.</summary>
    public IReadOnlyList<string> Members { get; } = members;
}
