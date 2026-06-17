using System.Collections;
using System.Linq.Expressions;

namespace Scry.Client;

/// <summary>
/// An <see cref="IQueryProvider"/> that captures LINQ expressions instead of executing them. The
/// captured tree is translated to the wire AST by the async terminal methods.
/// </summary>
sealed class ScryQueryProvider(ScryClient client, string root) :
    IQueryProvider
{
    public ScryClient Client { get; } = client;
    public string Root { get; } = root;

    public IQueryable CreateQuery(Expression expression) =>
        (IQueryable)Activator.CreateInstance(
            typeof(ScryQueryable<>).MakeGenericType(expression.Type.GetGenericArguments()[0]),
            this,
            expression)!;

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new ScryQueryable<TElement>(this, expression);

    public object Execute(Expression expression) =>
        throw NotSupported();

    public TResult Execute<TResult>(Expression expression) =>
        throw NotSupported();

    static NotSupportedException NotSupported() =>
        new("Scry queries do not execute synchronously. Use ToScryListAsync, " +
            "FirstScryAsync, CountScryAsync, etc.");
}

/// <summary>A deferred, capture-only queryable. Enumerating it synchronously is not supported.</summary>
sealed class ScryQueryable<T> :
    IOrderedQueryable<T>
{
    readonly ScryQueryProvider provider;

    public ScryQueryable(ScryQueryProvider provider) :
        this(provider, null)
    {
    }

    public ScryQueryable(ScryQueryProvider provider, Expression? expression)
    {
        this.provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }

    public Expression Expression { get; }

    public Type ElementType => typeof(T);

    public IQueryProvider Provider => provider;

    public IEnumerator<T> GetEnumerator() =>
        throw new NotSupportedException("Use ToScryListAsync to execute a Scry query.");

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
