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

    /// <summary>
    /// Maximum number of queries one batch request may carry. Default 20.
    /// </summary>
    /// <remarks>
    /// A batch is the one place a single request costs more than one query, so this is the bound that
    /// keeps it from being an amplifier: every other limit is per query and would otherwise apply to an
    /// arbitrary number of them. A batch over the limit is rejected whole, before any entry runs.
    /// </remarks>
    public int MaxBatchSize { get; set; } = 20;

    /// <summary>
    /// Maximum number of rows a streamed query may return, or null — the default — for no limit.
    /// </summary>
    /// <remarks>
    /// Null matches <c>ToListAsync</c>, which has never been bounded either: <see cref="MaxPageSize"/>
    /// caps <c>Take</c> and a page, not an unbounded enumeration. Streaming is the safer of the two
    /// server-side, since the rows are never buffered — but it holds a connection and a response open
    /// for as long as the client reads, which is the reason to offer a bound at all. A stream that
    /// reaches the limit ends with an error marker rather than a short result, so a client cannot
    /// mistake truncation for the end of the data.
    /// </remarks>
    public int? MaxStreamRows { get; set; }
    // end-snippet

    /// <summary>
    /// The collation applied when a client asks for a case-sensitive string comparison. Null — the
    /// default — rejects such a request instead, so the feature is opt-in per deployment.
    /// </summary>
    /// <remarks>
    /// This is deliberately a server setting rather than something a request carries. A collation
    /// cannot be a query parameter: it is emitted into the SQL text, so accepting one from a client
    /// would be the one place an attacker-supplied string reached SQL as anything but a parameter.
    /// Naming it here keeps the request to an intent — case-sensitive or not — and the SQL-affecting
    /// string under the server's control. Set it to a collation the database actually has, e.g.
    /// <c>Latin1_General_CS_AS</c> on SQL Server.
    /// </remarks>
    public string? CaseSensitiveCollation { get; set; }

    /// <summary>
    /// The collation applied when a client asks for a case-insensitive string comparison. Null — the
    /// default — rejects such a request. See <see cref="CaseSensitiveCollation"/> for why this is
    /// configured rather than requested; e.g. <c>Latin1_General_CI_AS</c> on SQL Server.
    /// </summary>
    public string? CaseInsensitiveCollation { get; set; }

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

    /// <summary>
    /// Attaches a row/instance policy to an entity, replacing any <c>[ReturnableWith]</c> on that same
    /// type. Like the attribute it also covers every opted-in type deriving from that one, whose own
    /// policies narrow further rather than replace this one.
    /// </summary>
    public void AddPolicy<TEntity, TPolicy>()
        where TPolicy : IReturnablePolicy<TEntity> =>
        Policies[typeof(TEntity)] = typeof(TPolicy);
}
