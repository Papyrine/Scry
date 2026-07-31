namespace Scry;

/// <summary>A registered queryable source (entity, view, or POCO).</summary>
public sealed class ScrySource(
    string name,
    Type clrType,
    SourceKind kind,
    IReadOnlyList<Type> policies,
    Func<DbContext, IServiceProvider, IQueryable> resolve)
{
    public string Name { get; } = name;
    public Type ClrType { get; } = clrType;
    public SourceKind Kind { get; } = kind;

    /// <summary>
    /// The row policies applied to this source before any client operator, ordered base-most first. A
    /// policy declared on a base type is in the chain of every opted-in type deriving from it, so a
    /// subclass cannot shed the one its base carries, and where several apply all of them narrow.
    /// </summary>
    public IReadOnlyList<Type> Policies { get; } = policies;

    public Func<DbContext, IServiceProvider, IQueryable> Resolve { get; } = resolve;
}
