/// <summary>
/// An <see cref="IQueryProvider"/> that captures LINQ expressions instead of executing them. The
/// captured tree is translated to the wire AST by the async terminal methods.
/// </summary>
sealed class QueryProvider(ScryClient client, string root, IReadOnlyList<string>? defaultProjection) :
    IQueryProvider
{
    public ScryClient Client { get; } = client;
    public string Root { get; } = root;

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
