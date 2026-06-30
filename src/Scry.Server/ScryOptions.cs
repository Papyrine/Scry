namespace Scry;

/// <summary>
/// Configures the server-side query executor: which model to expose, in-memory POCO sources,
/// row-level policies, and resource limits enforced during validation.
/// </summary>
public sealed class ScryOptions(Type contextType)
{
    /// <summary>Maximum number of rows a single query may request via <c>Take</c>. Default 1000.</summary>
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>Maximum navigation-path length allowed in a member expression. Default 4.</summary>
    public int MaxNavigationDepth { get; set; } = 4;

    /// <summary>Maximum number of operators in a query pipeline. Default 32.</summary>
    public int MaxPipelineLength { get; set; } = 32;

    /// <summary>Maximum expression nesting depth in a predicate. Default 32.</summary>
    public int MaxExpressionDepth { get; set; } = 32;

    internal Type ContextType { get; private set; } = contextType;

    internal Dictionary<Type, Func<IServiceProvider, IQueryable>> PocoSources { get; } = [];

    internal Dictionary<Type, Type> Policies { get; } = [];

    /// <summary>Registers the data for a <c>[QueryablePoco]</c> source, resolved per request.</summary>
    public void AddPocoSource<T>(Func<IServiceProvider, IEnumerable<T>> factory)
        where T : class =>
        PocoSources[typeof(T)] = services => factory(services).AsQueryable();

    /// <summary>Registers a fixed in-memory <c>[QueryablePoco]</c> source.</summary>
    public void AddPocoSource<T>(IEnumerable<T> items)
        where T : class =>
        AddPocoSource(_ => items);

    /// <summary>Attaches a row/instance policy to an entity, overriding any <c>[ReturnableWith]</c>.</summary>
    public void AddPolicy<TEntity, TPolicy>()
        where TPolicy : IReturnablePolicy<TEntity> =>
        Policies[typeof(TEntity)] = typeof(TPolicy);
}
