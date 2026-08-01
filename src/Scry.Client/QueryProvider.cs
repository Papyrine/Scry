/// <summary>
/// An <see cref="IQueryProvider"/> that captures LINQ expressions instead of executing them. The
/// captured tree is translated to the wire AST by the async terminal methods.
/// </summary>
sealed class QueryProvider(
    ScryClient client,
    string root,
    IReadOnlyList<string>? defaultProjection,
    ScryCall? call = null,
    ScryBatch? batch = null) :
    IQueryProvider
{
    public ScryClient Client { get; } = client;
    public string Root { get; } = root;

    /// <summary>
    /// The per-query header hooks, or null for a query that asked for none. Carried on the provider
    /// rather than in the captured expression because headers are not a wire concept: the translator
    /// would have nowhere to put them, and the server is never told they existed.
    /// </summary>
    public ScryCall? Call { get; } = call;

    /// <summary>
    /// The batch this query was deferred into, or null for one that sends on its own. Carried here for
    /// the same reason the header hooks are: which request carries a query is a transport concern the
    /// wire has no way — and no reason — to express.
    /// </summary>
    public ScryBatch? Batch { get; } = batch;

    /// <summary>The same source and client, with <paramref name="replacement"/> as its header hooks.</summary>
    public QueryProvider With(ScryCall replacement) =>
        new(Client, Root, DefaultProjection, replacement, Batch);

    /// <summary>The same source and client, deferred into <paramref name="replacement"/>.</summary>
    public QueryProvider With(ScryBatch replacement) =>
        new(Client, Root, DefaultProjection, Call, replacement);

    /// <summary>
    /// The scalar members of this source's query model, supplied by the generated entry point. A query
    /// that writes no <c>Select</c> projects these explicitly rather than letting the server pick, so
    /// the response comes back keyed by the names this client was generated with. Null for a
    /// hand-built source, which falls back to the server's default projection.
    /// </summary>
    public IReadOnlyList<string>? DefaultProjection { get; } = defaultProjection;

    public IQueryable CreateQuery(Expression expression) =>
        (IQueryable)Activator.CreateInstance(
            typeof(CaptureQueryable<>).MakeGenericType(expression.Type.GetGenericArguments()[0]),
            this,
            expression)!;

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new CaptureQueryable<TElement>(this, expression);

    public object Execute(Expression expression) =>
        throw NotSupported();

    public TResult Execute<TResult>(Expression expression) =>
        throw NotSupported();

    static NotSupportedException NotSupported() =>
        new("Scry queries do not execute synchronously. Use ToListAsync, FirstAsync, CountAsync, etc.");
}
