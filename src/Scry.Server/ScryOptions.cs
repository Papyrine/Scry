namespace Scry;

/// <summary>
/// Configures the server-side query executor: which model to expose, in-memory POCO sources,
/// row-level policies, and resource limits enforced during validation.
/// </summary>
public sealed class ScryOptions(Type contextType)
{
    // begin-snippet: scryOptionsLimits
    /// <summary>Maximum number of rows a single query may request via <c>Take</c>. Default 1000.</summary>
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>
    /// Page size applied to a paged query (<c>ToPageAsync</c>) that does not request one. Bounds an
    /// otherwise-unbounded page; the effective size is always capped by <see cref="MaxPageSize"/>. Default 100.
    /// </summary>
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>Maximum navigation-path length allowed in a member expression. Default 4.</summary>
    public int MaxNavigationDepth { get; set; } = 4;

    /// <summary>Maximum number of operators in a query pipeline. Default 32.</summary>
    public int MaxPipelineLength { get; set; } = 32;

    /// <summary>Maximum expression nesting depth in a predicate. Default 32.</summary>
    public int MaxExpressionDepth { get; set; } = 32;

    /// <summary>
    /// Maximum number of values a client may supply to a set-membership test (<c>Contains</c>, which
    /// becomes a SQL <c>IN</c>). Default 1000.
    /// </summary>
    public int MaxInValues { get; set; } = 1000;
    // end-snippet

    /// <summary>
    /// HMAC key used to sign keyset paging cursors. When null a random per-process key is used, so
    /// cursors do not survive a restart or work across multiple instances — set a stable key for a
    /// scaled-out or restart-tolerant deployment. Signing enforces the opaque-cursor contract; it is
    /// not an authorization control (a decoded cursor is always re-validated and policy-filtered).
    /// </summary>
    public byte[]? CursorSigningKey { get; set; }

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
