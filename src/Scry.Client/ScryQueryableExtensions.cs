using System.Text.Json;
using Scry.Wire;

namespace Scry.Client;

/// <summary>Async terminal operators that execute a captured Scry query against the server.</summary>
public static class ScryQueryableExtensions
{
    /// <summary>Executes the query and returns all rows.</summary>
    public static async Task<List<T>> ToScryListAsync<T>(this IQueryable<T> source, CancellationToken token = default)
    {
        var response = await Send(source, terminal: null, token);
        EnsureKind(response, ResultKind.List);
        return response.Payload.Deserialize<List<T>>(ScryJson.Options) ?? [];
    }

    /// <summary>Executes the query and returns the first row, or default if empty.</summary>
    public static Task<T?> FirstOrDefaultScryAsync<T>(this IQueryable<T> source, CancellationToken token = default) =>
        Single<T>(source, new FirstOp(OrDefault: true, Predicate: null), token);

    /// <summary>Executes the query and returns the first row.</summary>
    public static Task<T?> FirstScryAsync<T>(this IQueryable<T> source, CancellationToken token = default) =>
        Single<T>(source, new FirstOp(OrDefault: false, Predicate: null), token);

    /// <summary>Executes the query and returns the single row, or default if empty.</summary>
    public static Task<T?> SingleOrDefaultScryAsync<T>(this IQueryable<T> source, CancellationToken token = default) =>
        Single<T>(source, new SingleOp(OrDefault: true, Predicate: null), token);

    /// <summary>Executes the query and returns the single row.</summary>
    public static Task<T?> SingleScryAsync<T>(this IQueryable<T> source, CancellationToken token = default) =>
        Single<T>(source, new SingleOp(OrDefault: false, Predicate: null), token);

    /// <summary>Executes the query and returns the row count.</summary>
    public static async Task<int> CountScryAsync<T>(this IQueryable<T> source, CancellationToken token = default)
    {
        var response = await Send(source, new CountOp(), token);
        EnsureKind(response, ResultKind.Scalar);
        return response.Payload.Deserialize<int>(ScryJson.Options);
    }

    /// <summary>Executes the query and returns whether any rows match.</summary>
    public static async Task<bool> AnyScryAsync<T>(this IQueryable<T> source, CancellationToken token = default)
    {
        var response = await Send(source, new AnyOp(Predicate: null), token);
        EnsureKind(response, ResultKind.Scalar);
        return response.Payload.Deserialize<bool>(ScryJson.Options);
    }

    static async Task<T?> Single<T>(IQueryable<T> source, QueryOp terminal, CancellationToken token)
    {
        var response = await Send(source, terminal, token);
        EnsureKind(response, ResultKind.Single);
        return response.Payload.ValueKind == JsonValueKind.Null
            ? default
            : response.Payload.Deserialize<T>(ScryJson.Options);
    }

    static Task<QueryResponse> Send<T>(IQueryable<T> source, QueryOp? terminal, CancellationToken token)
    {
        if (source.Provider is not ScryQueryProvider provider)
        {
            throw new InvalidOperationException("This IQueryable is not a Scry source.");
        }

        var pipeline = new List<QueryOp>(QueryTranslator.Translate(source.Expression));
        if (terminal is not null)
        {
            pipeline.Add(terminal);
        }

        return provider.Client.SendAsync(QueryRequest.Create(provider.Root, pipeline), token);
    }

    static void EnsureKind(QueryResponse response, ResultKind expected)
    {
        if (response.Kind != expected)
        {
            throw new ScryWireException($"Expected a {expected} result but the server returned {response.Kind}.");
        }
    }
}
