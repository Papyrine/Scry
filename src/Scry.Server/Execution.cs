/// <summary>
/// How a built query is asked of its provider. A relational provider answers asynchronously, so a
/// request thread is not held on the database while a fold, a row, or a page is read; an in-memory
/// source has nothing to await and is asked directly. Every place the executor runs a query goes
/// through here, so the blocking processor surface and the awaiting endpoint differ only in which
/// of the two forms they call.
/// </summary>
static class Execution
{
    public static object? Run(IQueryable query, Expression call) =>
        query.Provider.Execute(call);

    public static ValueTask<object?> RunAsync(IQueryable query, Expression call, Cancel cancel)
    {
        if (query.Provider is not IAsyncQueryProvider asynchronous)
        {
            return new(query.Provider.Execute(call));
        }

        return runners.GetOrAdd(call.Type, Runner)(asynchronous, call, cancel);
    }

    // One closed runner per result type, since the provider answers a Task of that type and nothing
    // more general.
    static readonly ConcurrentDictionary<Type, Func<IAsyncQueryProvider, Expression, Cancel, ValueTask<object?>>> runners = new();

    static Func<IAsyncQueryProvider, Expression, Cancel, ValueTask<object?>> Runner(Type result) =>
        typeof(Execution)
            .GetMethod(nameof(RunTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(result)
            .CreateDelegate<Func<IAsyncQueryProvider, Expression, Cancel, ValueTask<object?>>>();

    static async ValueTask<object?> RunTyped<T>(IAsyncQueryProvider provider, Expression call, Cancel cancel) =>
        await provider.ExecuteAsync<Task<T>>(call, cancel);

    /// <summary>Reads every row, asynchronously where the provider can be.</summary>
    public static async ValueTask<List<T>> ReadAsync<T>(IQueryable<T> rows, Cancel cancel)
    {
        if (rows is not IAsyncEnumerable<T> asynchronous)
        {
            return rows.ToList();
        }

        var list = new List<T>();
        await foreach (var row in asynchronous.WithCancellation(cancel))
        {
            list.Add(row);
        }

        return list;
    }
}
