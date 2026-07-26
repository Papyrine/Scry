/// <summary>
/// An <see cref="IQueryProvider"/> that captures LINQ expressions instead of executing them. The
/// captured tree is translated to the wire AST by the async terminal methods.
/// </summary>
sealed class QueryProvider(ScryClient client, string root) :
    IQueryProvider
{
    public ScryClient Client { get; } = client;
    public string Root { get; } = root;

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
