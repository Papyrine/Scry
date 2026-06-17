using System.Collections;
using System.Linq.Expressions;

namespace Pneumatic.Client;

/// <summary>
/// An <see cref="IQueryProvider"/> that captures LINQ expressions instead of executing them. The
/// captured tree is translated to the wire AST by the async terminal methods.
/// </summary>
sealed class PneumaticQueryProvider(PneumaticClient client, string root) :
    IQueryProvider
{
    public PneumaticClient Client { get; } = client;
    public string Root { get; } = root;

    public IQueryable CreateQuery(Expression expression) =>
        (IQueryable)Activator.CreateInstance(
            typeof(PneumaticQueryable<>).MakeGenericType(expression.Type.GetGenericArguments()[0]),
            this,
            expression)!;

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new PneumaticQueryable<TElement>(this, expression);

    public object Execute(Expression expression) =>
        throw NotSupported();

    public TResult Execute<TResult>(Expression expression) =>
        throw NotSupported();

    static NotSupportedException NotSupported() =>
        new("Pneumatic queries do not execute synchronously. Use ToPneumaticListAsync, " +
            "FirstPneumaticAsync, CountPneumaticAsync, etc.");
}

/// <summary>A deferred, capture-only queryable. Enumerating it synchronously is not supported.</summary>
sealed class PneumaticQueryable<T> :
    IOrderedQueryable<T>
{
    readonly PneumaticQueryProvider provider;

    public PneumaticQueryable(PneumaticQueryProvider provider) :
        this(provider, null)
    {
    }

    public PneumaticQueryable(PneumaticQueryProvider provider, Expression? expression)
    {
        this.provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }

    public Expression Expression { get; }

    public Type ElementType => typeof(T);

    public IQueryProvider Provider => provider;

    public IEnumerator<T> GetEnumerator() =>
        throw new NotSupportedException("Use ToPneumaticListAsync to execute a Pneumatic query.");

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
