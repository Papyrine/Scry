namespace Scry;

/// <summary>A registered queryable source (entity, view, or POCO).</summary>
public sealed class ScrySource(
    string name,
    Type clrType,
    SourceKind kind,
    Type? policyType,
    Func<DbContext, IServiceProvider, IQueryable> resolve)
{
    public string Name { get; } = name;
    public Type ClrType { get; } = clrType;
    public SourceKind Kind { get; } = kind;
    public Type? PolicyType { get; } = policyType;
    public Func<DbContext, IServiceProvider, IQueryable> Resolve { get; } = resolve;
}
