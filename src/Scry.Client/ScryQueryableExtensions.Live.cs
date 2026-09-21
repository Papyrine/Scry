namespace Scry;

// In this class rather than one of its own: the analyzer tells a Scry terminal from anything else by
// the type that declares it, and everything these reuse to build a request and read a response is
// private to it.
public static partial class ScryQueryableExtensions
{
    // begin-snippet: liveTerminals
    /// <summary>
    /// The query's rows, now and again whenever they change. Each answer is the whole current result.
    /// </summary>
    public static ScryLiveQuery<IReadOnlyList<T>> Live<T>(this IQueryable<T> source)
    {
        var client = Client(Scry(source));
        var (plan, pipeline) = Plan(source);
        return Open<T, IReadOnlyList<T>>(
            source,
            terminal: null,
            pipeline,
            response =>
            {
                EnsureKind(response, ResultKind.List);
                return AttachmentBinder.Bind(Materialize<List<T>>(source, response), plan, client) ?? [];
            });
    }

    /// <summary>How many rows the query has, now and again whenever that changes.</summary>
    public static ScryLiveQuery<int> LiveCount<T>(this IQueryable<T> source) =>
        LiveScalar<T, int>(source, new CountOp());

    /// <summary>How many rows match <paramref name="predicate"/>, now and again whenever that changes.</summary>
    public static ScryLiveQuery<int> LiveCount<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate) =>
        LiveScalar<T, int>(source, new CountOp(Predicate(predicate)));

    /// <summary>How many rows the query has as a 64-bit integer, now and again whenever that changes.</summary>
    public static ScryLiveQuery<long> LiveLongCount<T>(this IQueryable<T> source) =>
        LiveScalar<T, long>(source, new LongCountOp());

    /// <summary>Whether the query has any rows, now and again whenever that changes.</summary>
    public static ScryLiveQuery<bool> LiveAny<T>(this IQueryable<T> source) =>
        LiveScalar<T, bool>(source, new AnyOp(Predicate: null));

    /// <summary>Whether any row matches <paramref name="predicate"/>, now and again whenever that changes.</summary>
    public static ScryLiveQuery<bool> LiveAny<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate) =>
        LiveScalar<T, bool>(source, new AnyOp(Predicate(predicate)));

    /// <summary>
    /// The query's first row, or default where it has none, now and again whenever that changes —
    /// which includes a different row becoming the first.
    /// </summary>
    public static ScryLiveQuery<T?> LiveFirstOrDefault<T>(this IQueryable<T> source) =>
        LiveSingle(source, new FirstOp(OrDefault: true, Predicate: null));

    /// <summary>The first row matching <paramref name="predicate"/>, or default, now and again whenever that changes.</summary>
    public static ScryLiveQuery<T?> LiveFirstOrDefault<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate) =>
        LiveSingle(source, new FirstOp(OrDefault: true, Predicate(predicate)));

    /// <summary>
    /// The query's only row, or default where it has none, now and again whenever that changes. What a
    /// detail view of one record wants: the record as it is, for as long as it is on screen.
    /// </summary>
    public static ScryLiveQuery<T?> LiveSingleOrDefault<T>(this IQueryable<T> source) =>
        LiveSingle(source, new SingleOp(OrDefault: true, Predicate: null));

    /// <summary>The only row matching <paramref name="predicate"/>, or default, now and again whenever that changes.</summary>
    public static ScryLiveQuery<T?> LiveSingleOrDefault<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate) =>
        LiveSingle(source, new SingleOp(OrDefault: true, Predicate(predicate)));
    // end-snippet

    static ScryLiveQuery<TValue> LiveScalar<T, TValue>(IQueryable<T> source, QueryOp terminal) =>
        Open<T, TValue>(
            source,
            terminal,
            pipeline: null,
            response =>
            {
                EnsureKind(response, ResultKind.Scalar);
                if (response.Payload.ValueKind == JsonValueKind.Null)
                {
                    return default!;
                }

                return Materialize<TValue>(source, response)!;
            });

    static ScryLiveQuery<T?> LiveSingle<T>(IQueryable<T> source, QueryOp terminal)
    {
        var client = Client(Scry(source));
        var (plan, pipeline) = Plan(source);
        return Open<T, T?>(
            source,
            terminal,
            pipeline,
            response =>
            {
                EnsureKind(response, ResultKind.Single);
                if (response.Payload.ValueKind == JsonValueKind.Null)
                {
                    return default;
                }

                return AttachmentBinder.BindRow(Materialize<T>(source, response), plan, client);
            });
    }

    // The request is built here, once, rather than each time the live query is asked for again: what
    // it closes over is read now, and every connection of its life asks the same question.
    static ScryLiveQuery<TResult> Open<T, TResult>(
        IQueryable<T> source,
        QueryOp? terminal,
        IReadOnlyList<QueryOp>? pipeline,
        Func<QueryResponse, TResult> read)
    {
        var provider = (QueryProvider)Scry(source).Provider;

        // A batch is one request answered once; a live query is one request answered for as long as
        // it is open. There is no shape that is both.
        if (provider.Batch is not null)
        {
            throw new NotSupportedException(
                "A live query cannot be batched: a batch is answered as one response, once. Drop InBatch from this query, or ask once with ToListAsync.");
        }

        var request = pipeline is null
            ? source.ToScryRequest(terminal)
            : Request(provider, pipeline, terminal, typeof(T));
        return new(provider.Client, request, provider.Call, read);
    }

    static IQueryable<T> Scry<T>(IQueryable<T> source)
    {
        if (source.Provider is QueryProvider)
        {
            return source;
        }

        throw new("This IQueryable is not a Scry source.");
    }
}
