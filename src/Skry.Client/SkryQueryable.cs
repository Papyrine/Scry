using System.Collections;
using System.Linq.Expressions;

namespace Skry.Client;

/// <summary>
/// An <see cref="IQueryProvider"/> that captures LINQ expressions instead of executing them. The
/// captured tree is translated to the wire AST by the async terminal methods.
/// </summary>
sealed class SkryQueryProvider(SkryClient client, string root) :
    IQueryProvider
{
    public SkryClient Client { get; } = client;
    public string Root { get; } = root;

    public IQueryable CreateQuery(Expression expression) =>
        (IQueryable)Activator.CreateInstance(
            typeof(SkryQueryable<>).MakeGenericType(expression.Type.GetGenericArguments()[0]),
            this,
            expression)!;

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new SkryQueryable<TElement>(this, expression);

    public object Execute(Expression expression) =>
        throw NotSupported();

    public TResult Execute<TResult>(Expression expression) =>
        throw NotSupported();

    static NotSupportedException NotSupported() =>
        new("Skry queries do not execute synchronously. Use ToSkryListAsync, " +
            "FirstSkryAsync, CountSkryAsync, etc.");
}

/// <summary>A deferred, capture-only queryable. Enumerating it synchronously is not supported.</summary>
sealed class SkryQueryable<T> :
    IOrderedQueryable<T>
{
    readonly SkryQueryProvider provider;

    public SkryQueryable(SkryQueryProvider provider) :
        this(provider, null)
    {
    }

    public SkryQueryable(SkryQueryProvider provider, Expression? expression)
    {
        this.provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }

    public Expression Expression { get; }

    public Type ElementType => typeof(T);

    public IQueryProvider Provider => provider;

    public IEnumerator<T> GetEnumerator() =>
        throw new NotSupportedException("Use ToSkryListAsync to execute a Skry query.");

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
