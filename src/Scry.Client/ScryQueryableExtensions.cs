namespace Scry;

/// <summary>Async terminal operators that execute a captured Scry query against the server.</summary>
public static class ScryQueryableExtensions
{
    /// <summary>Executes the query and returns all rows.</summary>
    public static async Task<List<T>> ToListAsync<T>(this IQueryable<T> source, Cancel cancel = default)
    {
        var response = await Send(source, terminal: null, cancel);
        EnsureKind(response, ResultKind.List);
        return Materialize<List<T>>(source, response) ?? [];
    }

    // The collection-shaping terminals below all send the same "enumerate to a list" request as
    // ToListAsync and reshape the materialised rows in memory — Scry has no streaming wire, so there
    // is no separate server op for them. The key/element selectors and comparers therefore run
    // client-side over the returned rows, exactly like the synchronous System.Linq equivalents.

    /// <summary>Executes the query and returns all rows as an array.</summary>
    public static async Task<T[]> ToArrayAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        [.. await ToListAsync(source, cancel)];

    /// <summary>Executes the query and returns all rows in a hash set.</summary>
    public static Task<HashSet<T>> ToHashSetAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        ToHashSetAsync(source, comparer: null, cancel);

    /// <summary>Executes the query and returns all rows in a hash set using the given comparer.</summary>
    public static async Task<HashSet<T>> ToHashSetAsync<T>(this IQueryable<T> source, IEqualityComparer<T>? comparer, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToHashSet(comparer);

    /// <summary>Executes the query and returns all rows keyed by <paramref name="keySelector"/>.</summary>
    public static async Task<Dictionary<TKey, T>> ToDictionaryAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector);

    /// <summary>Executes the query and returns all rows keyed by <paramref name="keySelector"/> using the given comparer.</summary>
    public static async Task<Dictionary<TKey, T>> ToDictionaryAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector, comparer);

    /// <summary>Executes the query and returns a dictionary of keys and elements projected from each row.</summary>
    public static async Task<Dictionary<TKey, TValue>> ToDictionaryAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector, elementSelector);

    /// <summary>Executes the query and returns a dictionary of keys and elements projected from each row, using the given comparer.</summary>
    public static async Task<Dictionary<TKey, TValue>> ToDictionaryAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default)
        where TKey : notnull =>
        (await ToListAsync(source, cancel)).ToDictionary(keySelector, elementSelector, comparer);

    /// <summary>Executes the query and returns all rows grouped into a lookup by <paramref name="keySelector"/>.</summary>
    public static async Task<ILookup<TKey, T>> ToLookupAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector);

    /// <summary>Executes the query and returns all rows grouped into a lookup by <paramref name="keySelector"/> using the given comparer.</summary>
    public static async Task<ILookup<TKey, T>> ToLookupAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector, comparer);

    /// <summary>Executes the query and returns a lookup of keys and elements projected from each row.</summary>
    public static async Task<ILookup<TKey, TValue>> ToLookupAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector, elementSelector);

    /// <summary>Executes the query and returns a lookup of keys and elements projected from each row, using the given comparer.</summary>
    public static async Task<ILookup<TKey, TValue>> ToLookupAsync<T, TKey, TValue>(this IQueryable<T> source, Func<T, TKey> keySelector, Func<T, TValue> elementSelector, IEqualityComparer<TKey>? comparer, Cancel cancel = default) =>
        (await ToListAsync(source, cancel)).ToLookup(keySelector, elementSelector, comparer);

    /// <summary>
    /// Executes the query and yields rows as they arrive, without the server or the client holding the
    /// whole result. The same request <see cref="ToListAsync{T}"/> sends, read from the streaming
    /// endpoint instead.
    /// </summary>
    /// <remarks>
    /// The rows are only the whole result if the enumeration runs to completion: a stream that ends
    /// without the server's closing marker throws rather than returning a short answer. Abandoning the
    /// enumeration early is fine — it disposes the response and the server stops — but it means what
    /// was read is a prefix, which is the caller's own doing rather than something to detect.
    /// </remarks>
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IQueryable<T> source,
        [EnumeratorCancellation] Cancel cancel = default)
    {
        if (source.Provider is not QueryProvider provider)
        {
            throw new("This IQueryable is not a Scry source.");
        }

        var client = provider.Client;
        await foreach (var row in client.StreamAsync(source.ToScryRequest(), provider.Call, cancel).WithCancellation(cancel))
        {
            yield return MaterializeRow<T>(source, row, client);
        }
    }

    // Mirrors Materialize: a row that will not read into the client's model is drift when the stamps
    // already disagree, and a real bug when they do not.
    static T MaterializeRow<T>(IQueryable source, JsonElement row, ScryClient client)
    {
        try
        {
            return ScryJson.DeserializeRow<T>(row, client.StreamAliases)!;
        }
        catch (JsonException exception)
            when (source.Provider is QueryProvider {Client.SchemaStale: true})
        {
            throw new ScryStaleClientException(
                $"A streamed row could not be read into this client's generated model: {exception.Message} " +
                "The server's queryable surface has changed — regenerate the client, or reload the deployed app.",
                exception);
        }
    }

    /// <summary>Executes the query and returns the first row, or default if empty.</summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new FirstOp(OrDefault: true, Predicate: null), cancel);

    /// <summary>Executes the query and returns the first row matching <paramref name="predicate"/>, or default if none does.</summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Single(source, new FirstOp(OrDefault: true, Predicate(predicate)), cancel);

    /// <summary>Executes the query and returns the first row.</summary>
    public static Task<T?> FirstAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new FirstOp(OrDefault: false, Predicate: null), cancel);

    /// <summary>Executes the query and returns the first row matching <paramref name="predicate"/>.</summary>
    public static Task<T?> FirstAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Single(source, new FirstOp(OrDefault: false, Predicate(predicate)), cancel);

    /// <summary>Executes the query and returns the single row, or default if empty.</summary>
    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new SingleOp(OrDefault: true, Predicate: null), cancel);

    /// <summary>Executes the query and returns the single row matching <paramref name="predicate"/>, or default if none does.</summary>
    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Single(source, new SingleOp(OrDefault: true, Predicate(predicate)), cancel);

    /// <summary>Executes the query and returns the single row.</summary>
    public static Task<T?> SingleAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new SingleOp(OrDefault: false, Predicate: null), cancel);

    /// <summary>Executes the query and returns the single row matching <paramref name="predicate"/>.</summary>
    public static Task<T?> SingleAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Single(source, new SingleOp(OrDefault: false, Predicate(predicate)), cancel);

    /// <summary>
    /// Executes the query and returns the last row. The query must be ordered — the server resolves
    /// "last" by reversing the ordering, so an unordered query is rejected.
    /// </summary>
    public static Task<T?> LastAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new LastOp(OrDefault: false, Predicate: null), cancel);

    /// <summary>Executes the query and returns the last row matching <paramref name="predicate"/>. The query must be ordered.</summary>
    public static Task<T?> LastAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Single(source, new LastOp(OrDefault: false, Predicate(predicate)), cancel);

    /// <summary>Executes the query and returns the last row, or default if empty. The query must be ordered.</summary>
    public static Task<T?> LastOrDefaultAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Single(source, new LastOp(OrDefault: true, Predicate: null), cancel);

    /// <summary>Executes the query and returns the last row matching <paramref name="predicate"/>, or default if none does. The query must be ordered.</summary>
    public static Task<T?> LastOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Single(source, new LastOp(OrDefault: true, Predicate(predicate)), cancel);

    // ElementAt is Skip + First rather than its own wire operator: the pipeline already expresses it
    // exactly, and skipping past the end yields no row — the same empty case First already handles.

    /// <summary>Executes the query and returns the row at <paramref name="index"/>.</summary>
    public static Task<T?> ElementAtAsync<T>(this IQueryable<T> source, int index, Cancel cancel = default) =>
        Single(source.Skip(index), new FirstOp(OrDefault: false, Predicate: null), cancel);

    /// <summary>Executes the query and returns the row at <paramref name="index"/>, or default if there is none.</summary>
    public static Task<T?> ElementAtOrDefaultAsync<T>(this IQueryable<T> source, int index, Cancel cancel = default) =>
        Single(source.Skip(index), new FirstOp(OrDefault: true, Predicate: null), cancel);

    /// <summary>Executes the query and returns the row count.</summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Scalar<T, int>(source, new CountOp(), cancel);

    /// <summary>Executes the query and returns the number of rows matching <paramref name="predicate"/>.</summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Scalar<T, int>(source, new CountOp(Predicate(predicate)), cancel);

    /// <summary>Executes the query and returns the row count as a 64-bit integer.</summary>
    public static Task<long> LongCountAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Scalar<T, long>(source, new LongCountOp(), cancel);

    /// <summary>Executes the query and returns the number of rows matching <paramref name="predicate"/> as a 64-bit integer.</summary>
    public static Task<long> LongCountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Scalar<T, long>(source, new LongCountOp(Predicate(predicate)), cancel);

    /// <summary>Executes the query and returns whether any rows match.</summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Scalar<T, bool>(source, new AnyOp(Predicate: null), cancel);

    /// <summary>Executes the query and returns whether any row matches <paramref name="predicate"/>.</summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Scalar<T, bool>(source, new AnyOp(Predicate(predicate)), cancel);

    /// <summary>Executes the query and returns whether every row matches <paramref name="predicate"/>. True when no rows match the query.</summary>
    public static Task<bool> AllAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, Cancel cancel = default) =>
        Scalar<T, bool>(source, new AllOp(Predicate(predicate)), cancel);

    // Sum and Average carry one overload per numeric type, mirroring System.Linq: the value the server
    // computes has a type of its own (Average over integers is a double), so the selector's type has to
    // pick the returned type rather than being echoed back. Min/Max stay generic — their result is
    // always the selected type — and return null for an empty sequence rather than faulting.

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<int> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, int>> selector, Cancel cancel = default) =>
        Aggregate<T, int>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<int?> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, int?>> selector, Cancel cancel = default) =>
        Aggregate<T, int?>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<long> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, long>> selector, Cancel cancel = default) =>
        Aggregate<T, long>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<long?> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, long?>> selector, Cancel cancel = default) =>
        Aggregate<T, long?>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<float> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, float>> selector, Cancel cancel = default) =>
        Aggregate<T, float>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<float?> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, float?>> selector, Cancel cancel = default) =>
        Aggregate<T, float?>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<double> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, double>> selector, Cancel cancel = default) =>
        Aggregate<T, double>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<double?> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, double?>> selector, Cancel cancel = default) =>
        Aggregate<T, double?>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<decimal> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector, Cancel cancel = default) =>
        Aggregate<T, decimal>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the sum of the selected values.</summary>
    public static Task<decimal?> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal?>> selector, Cancel cancel = default) =>
        Aggregate<T, decimal?>(source, AggregateFn.Sum, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<double> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, int>> selector, Cancel cancel = default) =>
        Aggregate<T, double>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<double?> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, int?>> selector, Cancel cancel = default) =>
        Aggregate<T, double?>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<double> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, long>> selector, Cancel cancel = default) =>
        Aggregate<T, double>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<double?> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, long?>> selector, Cancel cancel = default) =>
        Aggregate<T, double?>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<float> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, float>> selector, Cancel cancel = default) =>
        Aggregate<T, float>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<float?> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, float?>> selector, Cancel cancel = default) =>
        Aggregate<T, float?>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<double> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, double>> selector, Cancel cancel = default) =>
        Aggregate<T, double>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<double?> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, double?>> selector, Cancel cancel = default) =>
        Aggregate<T, double?>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<decimal> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector, Cancel cancel = default) =>
        Aggregate<T, decimal>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the average of the selected values.</summary>
    public static Task<decimal?> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal?>> selector, Cancel cancel = default) =>
        Aggregate<T, decimal?>(source, AggregateFn.Average, selector, cancel);

    /// <summary>Executes the query and returns the smallest selected value, or default if there are no rows.</summary>
    public static Task<TValue?> MinAsync<T, TValue>(this IQueryable<T> source, Expression<Func<T, TValue>> selector, Cancel cancel = default) =>
        Aggregate<T, TValue?>(source, AggregateFn.Min, selector, cancel);

    /// <summary>Executes the query and returns the largest selected value, or default if there are no rows.</summary>
    public static Task<TValue?> MaxAsync<T, TValue>(this IQueryable<T> source, Expression<Func<T, TValue>> selector, Cancel cancel = default) =>
        Aggregate<T, TValue?>(source, AggregateFn.Max, selector, cancel);

    /// <summary>
    /// Executes the query and returns a bounded page using the server's default page size, plus
    /// whether further rows exist. Advance to the next page with <c>Skip</c>.
    /// </summary>
    public static Task<ScryPage<T>> ToPageAsync<T>(this IQueryable<T> source, Cancel cancel = default) =>
        Page(source, new PageOp(Size: null), cancel);

    /// <summary>
    /// Executes the query and returns a page of at most <paramref name="pageSize"/> rows (capped by the
    /// server's <c>MaxPageSize</c>), plus whether further rows exist. Advance with <c>Skip</c>.
    /// </summary>
    public static Task<ScryPage<T>> ToPageAsync<T>(this IQueryable<T> source, int pageSize, Cancel cancel = default) =>
        Page(source, new PageOp(pageSize), cancel);

    /// <summary>
    /// Executes the query and returns a page of at most <paramref name="pageSize"/> rows, resuming from
    /// <paramref name="cursor"/> (keyset paging). Pass the <see cref="ScryPage{T}.Cursor"/> from the
    /// previous page; a null cursor starts from the beginning. The query must be ordered.
    /// </summary>
    public static Task<ScryPage<T>> ToPageAsync<T>(this IQueryable<T> source, int pageSize, string? cursor, Cancel cancel = default) =>
        Page(source, new PageOp(pageSize, cursor), cancel);

    static async Task<ScryPage<T>> Page<T>(IQueryable<T> source, PageOp terminal, Cancel cancel)
    {
        var response = await Send(source, terminal, cancel);
        EnsureKind(response, ResultKind.Page);
        return Materialize<ScryPage<T>>(source, response) ??
               throw new ScryWireException("Page result deserialized to null.");
    }

    static Node Predicate<T>(Expression<Func<T, bool>> predicate) =>
        QueryTranslator.TranslateLambda(predicate);

    static Task<TValue?> Aggregate<T, TValue>(IQueryable<T> source, AggregateFn function, LambdaExpression selector, Cancel cancel) =>
        Scalar<T, TValue>(source, new AggregateOp(function, QueryTranslator.TranslateLambda(selector)), cancel);

    /// <summary>
    /// Sends a terminal that produces a single scalar. A null payload — an aggregate over no rows —
    /// yields the default rather than a deserialization failure.
    /// </summary>
    static async Task<TValue?> Scalar<T, TValue>(IQueryable<T> source, QueryOp terminal, Cancel cancel)
    {
        var response = await Send(source, terminal, cancel);
        EnsureKind(response, ResultKind.Scalar);
        if (response.Payload.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        return response.Payload.Deserialize<TValue>(ScryJson.Options);
    }

    static async Task<T?> Single<T>(IQueryable<T> source, QueryOp terminal, Cancel cancel)
    {
        var response = await Send(source, terminal, cancel);
        EnsureKind(response, ResultKind.Single);
        if (response.Payload.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        return Materialize<T>(source, response);
    }

    /// <summary>
    /// Reads a result payload into the client's generated model, classifying a failure the schema
    /// stamp already attributes to drift.
    /// </summary>
    /// <remarks>
    /// The alias machinery bridges renames, but not every model change: a widened numeric whose value
    /// now overflows the old property, a member that became nullable, a scalar whose representation
    /// changed. Those surface here as a parse failure. When the stamp already shows the client behind
    /// the server — recorded from the response header before this payload is touched — the cause is
    /// drift rather than a wire fault, so it joins every other stale-client failure under one
    /// exception. When the stamps agree, the original propagates untouched: a current client failing
    /// to read a current payload is a real bug and must stay loud.
    /// </remarks>
    static T? Materialize<T>(IQueryable source, QueryResponse response)
    {
        try
        {
            return ScryJson.DeserializePayload<T>(response);
        }
        catch (JsonException exception)
            when (source.Provider is QueryProvider {Client.SchemaStale: true})
        {
            throw new ScryStaleClientException(
                $"The query result could not be read into this client's generated model: {exception.Message} " +
                "The server's queryable surface has changed — regenerate the client, or reload the deployed app.",
                exception);
        }
    }

    /// <summary>
    /// Translates a captured Scry query into its wire <see cref="QueryRequest"/> without executing it.
    /// Intended for tooling that needs to inspect or forward the serialized query (e.g. the query
    /// explorer); the terminal operators above are what application code normally uses.
    /// </summary>
    public static QueryRequest ToScryRequest<T>(this IQueryable<T> source, QueryOp? terminal = null)
    {
        if (source.Provider is not QueryProvider provider)
        {
            throw new("This IQueryable is not a Scry source.");
        }

        var pipeline = new List<QueryOp>(QueryTranslator.Translate(source.Expression));
        AddDefaultProjection(pipeline, provider, terminal, typeof(T));
        if (terminal is not null)
        {
            pipeline.Add(terminal);
        }

        return QueryRequest.Create(provider.Root, pipeline, provider.Client.SchemaStamp);
    }

    /// <summary>
    /// Appends a projection over the source's scalar members when the query wrote no <c>Select</c>.
    /// Without it the server picks the response keys from its own model, which a client generated
    /// before a member rename cannot read; naming them here makes the response keys the client's own.
    /// </summary>
    static void AddDefaultProjection(
        List<QueryOp> pipeline,
        QueryProvider provider,
        QueryOp? terminal,
        Type element)
    {
        // A grouped query's Select is the aggregate projection and is mandatory; the scalar terminals
        // return a single value and project nothing. Neither wants a member projection bolted on. A
        // terminal carrying its own predicate is rejected server-side once a Select is present, so it
        // falls back to the server's default projection rather than being made invalid.
        // An operator that changes which row is being read leaves the source's own member list
        // describing rows the query no longer returns, so the members come off the element type the
        // query ended up with. A hand-built source has no model to read them from and falls back to
        // the server's default projection.
        var members = pipeline.Any(_ => _ is OfTypeOp or SelectManyOp)
            ? element.GetCustomAttribute<ScryModelAttribute>()?.Members
            : provider.DefaultProjection;

        if (members is not { Count: > 0 } ||
            terminal is CountOp or LongCountOp or AnyOp or AllOp or AggregateOp ||
            terminal is FirstOp { Predicate: not null } or SingleOp { Predicate: not null } or LastOp { Predicate: not null } ||
            // A join carries its own projection, since a member has to name which side it reads.
            pipeline.Any(_ => _ is SelectOp or GroupByOp or JoinOp or SetOp))
        {
            return;
        }

        pipeline.Add(
            new SelectOp(
                new([..members.Select(_ => new ProjectionMember(_, new NodeValue(new MemberNode([_]))))])));
    }

    static Task<QueryResponse> Send<T>(IQueryable<T> source, QueryOp? terminal, Cancel cancel)
    {
        if (source.Provider is not QueryProvider provider)
        {
            throw new("This IQueryable is not a Scry source.");
        }

        return provider.Client.SendAsync(source.ToScryRequest(terminal), provider.Call, cancel);
    }

    static void EnsureKind(QueryResponse response, ResultKind expected)
    {
        if (response.Kind != expected)
        {
            throw new ScryWireException($"Expected a {expected} result but the server returned {response.Kind}.");
        }
    }
}
