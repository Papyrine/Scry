/// <summary>A deferred, capture-only queryable. Enumerating it synchronously is not supported.</summary>
sealed class CaptureQueryable<T> :
    IOrderedQueryable<T>
{
    readonly QueryProvider provider;

    public CaptureQueryable(QueryProvider provider) :
        this(provider, null)
    {
    }

    public CaptureQueryable(QueryProvider provider, Expression? expression)
    {
        this.provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }

    public Expression Expression { get; }

    public Type ElementType => typeof(T);

    public IQueryProvider Provider => provider;

    public IEnumerator<T> GetEnumerator() =>
        throw new NotSupportedException("Use ToListAsync to execute a Scry query.");

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
